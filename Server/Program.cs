﻿using System;
using System.Collections.Generic;
using System.Data.Entity.Migrations;
using System.Data.Entity.Migrations.Infrastructure;
using System.IO;
using System.Linq;
using System.Reflection;
using CitizenFX.Core;
using CitizenFX.Core.Native;
using NFive.SDK.Server.Configuration;
using NFive.SDK.Server.Controllers;
using NFive.Server.Configuration;
using NFive.Server.Controllers;
using NFive.Server.Diagnostics;
using NFive.Server.Events;
using NFive.Server.Rpc;
using JetBrains.Annotations;
using NFive.SDK.Plugins;
using NFive.SDK.Plugins.Configuration;
using NFive.SDK.Plugins.Models;
using NFive.SDK.Server.Migrations;

namespace NFive.Server
{
	[UsedImplicitly]
	public class Program : BaseScript
	{
		private readonly Logger logger = new Logger();
		private readonly Dictionary<Name, List<Controller>> controllers = new Dictionary<Name, List<Controller>>();

		public Program()
		{
			// Set the AppDomain working directory to the current resource root
			Environment.CurrentDirectory = FileManager.ResolveResourcePath();

			var config = ConfigurationManager.Load<CoreConfiguration>("nfive.yml");

			ServerConfiguration.LogLevel = config.Log.Level;
			API.SetMapName(config.Display.Map);
			API.SetGameType(config.Display.Map);

			// Setup RPC handlers
			RpcManager.Configure(this.EventHandlers);

			var events = new EventManager();

			// TODO: Create a dedicated RconController and move this horrible mess out of here.
			new RpcHandler().Event("rconCommand").OnRaw(new Action<string, List<object>>(
				(command, objArgs) =>
				{
					if (command.ToLowerInvariant() != "reload") return;
					try
					{
						new Logger("NFive").Debug("Reload command called");
						var args = objArgs.Select(a => new Name(a.ToString())).ToList();
						if (args.Count == 0) args = this.controllers.Keys.ToList();
						foreach (var pluginName in args)
						{
							if (!this.controllers.ContainsKey(pluginName)) continue;
							foreach (var controller in this.controllers[pluginName])
							{
								var controllerType = controller.GetType();
								if (controllerType.BaseType != null && controllerType.BaseType.IsGenericType && controllerType.BaseType.GetGenericTypeDefinition() == typeof(ConfigurableController<>))
								{
									controllerType.GetMethods().FirstOrDefault(m => m.DeclaringType == controllerType && m.Name == "Reload")?.Invoke(
										controller,
										new[]
										{
											ConfigurationManager.InitializeConfig(pluginName, controllerType.BaseType.GetGenericArguments()[0])
										}
									);
								}
								else
								{
									controller.Reload();
								}
							}
						}
					}
					catch (Exception ex)
					{

					}
					finally
					{
						Function.Call(Hash.CANCEL_EVENT);
					}
				}));

			// Load core controllers
			var dbController = new DatabaseController(new Logger("Database"), events, new RpcHandler(), ConfigurationManager.Load<DatabaseConfiguration>("database.yml"));
			this.controllers.Add(new Name("NFive/Server"), new List<Controller> { dbController });

			// Resolve dependencies
			var graph = DefinitionGraph.Load("nfive.lock");

			// Load plugins into the AppDomain
			foreach (var plugin in graph.Definitions)
			{
				this.logger.Info($"Loading {plugin.FullName}");

				// Load include files
				foreach (string includeName in plugin.Server?.Include ?? new List<string>())
				{
					string includeFile = Path.Combine("plugins", plugin.Name.Vendor, plugin.Name.Project, $"{includeName}.dll");
					if (!File.Exists(includeFile)) throw new FileNotFoundException(includeFile);

					AppDomain.CurrentDomain.Load(File.ReadAllBytes(includeFile));
				}

				// Load main files
				foreach (string mainName in plugin.Server?.Main ?? new List<string>())
				{
					string mainFile = Path.Combine("plugins", plugin.Name.Vendor, plugin.Name.Project, $"{mainName}.net.dll");
					if (!File.Exists(mainFile)) throw new FileNotFoundException(mainFile);

					var types = Assembly.LoadFrom(mainFile).GetTypes().Where(t => !t.IsAbstract && t.IsClass).ToList();

					//this.logger.Debug($"{mainName}: {types.Count} {string.Join(Environment.NewLine, types)}");

					// Find migrations
					foreach (Type migrationType in types.Where(t => t.BaseType != null && t.BaseType.IsGenericType && t.BaseType.GetGenericTypeDefinition() == typeof(MigrationConfiguration<>)))
					{
						var configuration = (DbMigrationsConfiguration)Activator.CreateInstance(migrationType);
						var migrator = new DbMigrator(configuration);

						if (!migrator.GetPendingMigrations().Any()) continue;

						if (!ServerConfiguration.AutomaticMigrations) throw new MigrationsPendingException($"Plugin {plugin.FullName} has pending migrations but automatic migrations are disabled");

						this.logger.Debug($"{mainName}: Running migrations {string.Join(", ", migrator.GetPendingMigrations())}");

						migrator.Update();
					}

					// Find controllers
					foreach (Type controllerType in types.Where(t => t.IsSubclassOf(typeof(Controller)) || t.IsSubclassOf(typeof(ConfigurableController<>))))
					{
						List<object> constructorArgs = new List<object>
						{
							new Logger($"Plugin|{plugin.Name}"),
							events,
							new RpcHandler()
						};

						// Check if controller is configurable
						if (controllerType.BaseType != null && controllerType.BaseType.IsGenericType && controllerType.BaseType.GetGenericTypeDefinition() == typeof(ConfigurableController<>))
						{
							// Initialize the controller configuration
							constructorArgs.Add(ConfigurationManager.InitializeConfig(plugin.Name, controllerType.BaseType.GetGenericArguments()[0]));
						}

						// Construct controller instance
						Controller controller = (Controller)Activator.CreateInstance(controllerType, constructorArgs.ToArray());

						if (!this.controllers.ContainsKey(plugin.Name)) this.controllers.Add(plugin.Name, new List<Controller>());
						this.controllers[plugin.Name].Add(controller);
					}
				}
			}

			events.Raise("serverInitialized");

			this.logger.Info($"{graph.Definitions.Count} plugins loaded, {this.controllers.Count} controller(s) created");
		}
	}
}
