using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using PortainerMCPSharp.Configuration;
using PortainerMCPSharp.Hosting;
using PortainerMCPSharp.Services;
using Serilog;

namespace PortainerMCPSharp;

public static class Program
{
    public static int Main(string[] args)
    {
        // When running as a Windows Service the working directory is
        // C:\Windows\System32, so resolve config and logs relative to the exe.
        var contentRoot = GetContentRoot();
        var isService = WindowsServiceHelpers.IsWindowsService();
        if (!isService)
        {
            McpSharpIcon.ApplyConsoleWindowIcon();
        }

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Console()
            .WriteTo.File(
                Path.Combine(contentRoot, "logs", "portainermcp-bootstrap-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                shared: true)
            .CreateBootstrapLogger();

        try
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                Args = args,
                ContentRootPath = contentRoot,
            });

            builder.Configuration
                .SetBasePath(contentRoot)
                .AddJsonFile(ResolveConfigFile(contentRoot, "appsettings.json"), optional: true, reloadOnChange: true)
                .AddJsonFile(ResolveConfigFile(contentRoot, $"appsettings.{builder.Environment.EnvironmentName}.json"), optional: true, reloadOnChange: true)
                .AddJsonFile(ResolveConfigFile(contentRoot, "appsettings.Local.json"), optional: true, reloadOnChange: true)
                .AddJsonFile(ResolveConfigFile(contentRoot, "PortainerMCPSharp.json"), optional: true, reloadOnChange: true)
                .AddJsonFile(ResolveConfigFile(contentRoot, $"PortainerMCPSharp.{builder.Environment.EnvironmentName}.json"), optional: true, reloadOnChange: true)
                .AddJsonFile(ResolveConfigFile(contentRoot, "PortainerMCPSharp.Local.json"), optional: true, reloadOnChange: true)
                .AddEnvironmentVariables()
                .AddEnvironmentVariables(prefix: "PORTAINERMCP_")
                .AddCommandLine(args);

            if (isService)
            {
                var svcOptions = builder.Configuration.GetSection(ServerOptions.SectionName).Get<ServerOptions>() ?? new ServerOptions();
                builder.Host.UseWindowsService(o => o.ServiceName = svcOptions.WindowsServiceName);
            }

            builder.Host.UseSerilog((ctx, services, cfg) => cfg
                .ReadFrom.Configuration(ctx.Configuration)
                .ReadFrom.Services(services)
                .Enrich.FromLogContext());

            builder.Services.Configure<PortainerOptions>(
                builder.Configuration.GetSection(PortainerOptions.SectionName));
            builder.Services.Configure<ServerOptions>(
                builder.Configuration.GetSection(ServerOptions.SectionName));

            builder.Services.AddSingleton<PortainerRegistry>();
            builder.Services.AddSingleton<SafetyGate>();
            builder.Services.AddSingleton<Redaction>();
            builder.Services.AddSingleton<StackResolver>();

            builder.Services
                .AddMcpServer()
                .WithHttpTransport()
                .WithToolsFromAssembly();

            var server = builder.Configuration.GetSection(ServerOptions.SectionName).Get<ServerOptions>() ?? new ServerOptions();
            builder.WebHost.ConfigureKestrel(k =>
            {
                if (string.Equals(server.Host, "localhost", StringComparison.OrdinalIgnoreCase))
                {
                    k.ListenLocalhost(server.Port);
                }
                else if (IPAddress.TryParse(server.Host, out var ip))
                {
                    k.Listen(ip, server.Port);
                }
                else
                {
                    k.ListenAnyIP(server.Port);
                }
            });

            var app = builder.Build();

            app.UseSerilogRequestLogging();

            // Surface any swallowed exceptions from the host as fatal log entries.
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
                Log.Fatal(e.ExceptionObject as Exception, "Unhandled exception in AppDomain");
            TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                Log.Error(e.Exception, "Unobserved task exception");
                e.SetObserved();
            };

            var registry = app.Services.GetRequiredService<PortainerRegistry>();
            var gate = app.Services.GetRequiredService<SafetyGate>();
            var options = gate.Options;

            LogStartup(
                "PortainerMCPSharp",
                $"http://{server.Host}:{server.Port}{server.Path}",
                "HTTP (Streamable)",
                isService ? "WindowsService" : "Console",
                contentRoot,
                registry,
                options);

            app.UseMiddleware<McpPasswordMiddleware>();

            app.MapFavicon();
            app.MapGet("/healthz", () => new
            {
                status = "ok",
                server = "PortainerMCPSharp",
                path = server.Path,
                readOnly = options.ReadOnly,
                instances = registry.Instances.Values.Select(i => new
                {
                    alias = i.Alias,
                    baseUrl = i.BaseUrl,
                    credential = i.CredentialKind,
                }),
                timeUtc = DateTimeOffset.UtcNow,
            });
            app.MapMcp(server.Path);

            app.Run();
            return 0;
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Server terminated unexpectedly");
            return 1;
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    /// <summary>
    /// Emits the operating posture so a misconfigured deployment is obvious from the first
    /// screen of logs rather than from a surprising tool refusal an hour later.
    /// </summary>
    private static void LogStartup(
        string serviceName,
        string endpoint,
        string transport,
        string mode,
        string contentRoot,
        PortainerRegistry registry,
        PortainerOptions options)
    {
        var log = Log.ForContext("SourceContext", serviceName + ".Startup");
        log.Information("{ServiceName} startup", serviceName);
        log.Information("  Endpoint: {Endpoint}", endpoint);
        log.Information("  Transport: {Transport}", transport);
        log.Information("  Mode: {Mode}", mode);
        log.Information("  Read-only: {ReadOnly}", options.ReadOnly);

        if (!registry.HasInstances)
        {
            log.Warning("  Instances: NONE CONFIGURED — add Portainer:Instances entries with BaseUrl + ApiKey");
        }
        else
        {
            foreach (var instance in registry.Instances.Values)
            {
                var isDefault = string.Equals(instance.Alias, registry.DefaultAlias, StringComparison.OrdinalIgnoreCase);
                log.Information(
                    "  Instance: {Alias}{Default} {BaseUrl} ({Credential})",
                    instance.Alias, isDefault ? " (default)" : string.Empty, instance.BaseUrl, instance.CredentialKind);

                if (instance.CredentialKind == "none")
                {
                    log.Warning(
                        "    No credential for '{Alias}' — set Portainer:Instances:<n>:ApiKey (preferred) or Username + Password",
                        instance.Alias);
                }
            }
        }

        log.Information(
            "  Stack deploy: {Deploy}   lifecycle: {Lifecycle}   delete: {Delete}",
            options.AllowStackDeploy, options.AllowStackLifecycle, options.AllowStackDelete);
        log.Information(
            "  Container lifecycle: {Lifecycle}   create: {Create}   removal: {Removal}",
            options.AllowContainerLifecycle, options.AllowContainerCreate, options.AllowContainerRemoval);
        log.Information(
            "  Image pull: {Pull}   image removal: {ImageRemoval}   prune: {Prune}",
            options.AllowImagePull, options.AllowImageRemoval, options.AllowPrune);
        log.Information(
            "  Volume mgmt: {Volumes}   network mgmt: {Networks}   swarm mutation: {Swarm}",
            options.AllowVolumeManagement, options.AllowNetworkManagement, options.AllowSwarmMutation);
        log.Information("  Protect Portainer itself: {Protect}", options.ProtectPortainerItself);
        log.Information("  Redact secrets: {Redact}", options.RedactSecrets);

        if (options.AllowedEnvironmentIds.Count > 0)
            log.Information("  Allowed environments: {Ids}", string.Join(", ", options.AllowedEnvironmentIds));
        if (options.BlockedEnvironmentIds.Count > 0)
            log.Information("  Blocked environments: {Ids}", string.Join(", ", options.BlockedEnvironmentIds));
        if (options.AllowedStacks.Count > 0)
            log.Information("  Allowed stacks: {Names}", string.Join(", ", options.AllowedStacks));
        if (options.BlockedStacks.Count > 0)
            log.Information("  Blocked stacks: {Names}", string.Join(", ", options.BlockedStacks));

        log.Information("  Content root: {ContentRoot}", contentRoot);
    }

    private static string GetContentRoot() =>
        Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;

    private static string ResolveConfigFile(string contentRoot, string fileName)
    {
        if (File.Exists(Path.Combine(contentRoot, fileName)))
        {
            return fileName;
        }

        try
        {
            var match = Directory.EnumerateFiles(contentRoot, "*", SearchOption.TopDirectoryOnly)
                .FirstOrDefault(path => string.Equals(Path.GetFileName(path), fileName, StringComparison.OrdinalIgnoreCase));

            return match is null ? fileName : Path.GetFileName(match);
        }
        catch (DirectoryNotFoundException)
        {
            return fileName;
        }
    }
}
