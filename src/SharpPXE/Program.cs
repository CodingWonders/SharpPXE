// =====================================================================================
//  Program.cs - startup: command line, configuration, and the .NET generic host
// =====================================================================================
//
//  This file uses C# "top-level statements": no Main method or Program class to write.
//  The compiler wraps the code below in one.
//
//  HOW THE PIECES ARE ASSEMBLED
//  ----------------------------
//  The server is three independent services that share the same configuration:
//
//      DhcpServer   (BackgroundService)  UDP 67 + 4011
//      TftpServer   (BackgroundService)  UDP 69 + one port per transfer
//      Kestrel HTTP (WebApplication)     TCP 80
//
//  The .NET "generic host" (Microsoft.Extensions.Hosting) runs them all:
//    - Dependency injection: services list what they need in their constructors
//      (Config, BootPolicy, FileProvider), and the host creates them in the right order.
//      AddSingleton = one shared instance for the whole program.
//    - Lifetime: the host starts every IHostedService, waits for Ctrl+C or a service-stop
//      request, then cancels their CancellationToken so the sockets close cleanly.
//    - Windows service support: AddWindowsService() does nothing when you run in a console,
//      and makes the same exe work as a service when the Service Control Manager starts it.
//
//  NATIVE AOT
//  ----------
//  <PublishAot>true</PublishAot> in the .csproj compiles everything (our code, ASP.NET Core
//  and the runtime) to one native x64/ARM64 exe. Nothing needs to be installed, it starts
//  fast, and it's a real 64-bit binary, so it doesn't need WOW64. The cost is no runtime
//  reflection, which is why the code avoids reflection-based features (config binding, MVC).
// =====================================================================================

using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SharpPXE;
using SharpPXE.Dhcp;
using SharpPXE.Http;
using SharpPXE.Tftp;

const string ServiceName = "SharpPXE";

string configPath = Path.Combine(AppContext.BaseDirectory, "SharpPXE.ini");
for (int i = 0; i < args.Length; i++)
{
    switch (args[i].ToLowerInvariant())
    {
        case "--config" or "-c" when i + 1 < args.Length:
            configPath = Path.GetFullPath(args[++i]);
            break;
        case "--install":
            return InstallService(configPath);
        case "--uninstall":
            return UninstallService();
        case "--firewall":
        {
            // Create or repair the rule and exit. The GUI runs this elevated.
            var state = Firewall.Ensure(Environment.ProcessPath!, out var msg);
            Console.WriteLine($"Firewall: {msg}");
            return state is Firewall.State.Ok or Firewall.State.Created ? 0 : 1;
        }
        case "--check-firewall":
            // 0 = rule present for this exe, 2 = missing (no admin rights needed to check)
            return Firewall.RuleMatches(Environment.ProcessPath!) ? 0 : 2;
        case "--help" or "-h" or "/?":
            Console.WriteLine("""
                SharpPXE - proxyDHCP / DHCP, TFTP and HTTP boot server

                  SharpPXE [--config <file.ini>]   run in the console (Ctrl+C to stop)
                  SharpPXE --install [--config f]  install as a Windows service + firewall rule (admin)
                  SharpPXE --uninstall             remove the service and firewall rule (admin)
                  SharpPXE --firewall              create/repair the inbound firewall rule (admin)
                  SharpPXE --check-firewall        exit code 0 if the rule exists, 2 if not

                Every start also checks the firewall rule and creates it when running
                with administrator rights (turn off with [server] firewall=0).
                """);
            return 0;
    }
}

Config cfg;
try { cfg = Config.Load(configPath); }
catch (Exception ex) { Log.Error(ex.Message); return 1; }

Log.Verbose = cfg.Verbose;
if (cfg.LogFile is not null) Log.OpenFile(cfg.LogFile);
Log.Info($"SharpPXE {typeof(Config).Assembly.GetName().Version?.ToString(3)} - config {configPath}");
Log.Info($"Mode: {cfg.Mode}, root: {cfg.RootFullPath}");

if (!Directory.Exists(cfg.RootFullPath)) Log.Warn($"Root folder does not exist: {cfg.RootFullPath}");
var ifaces = cfg.ResolveInterfaces();
if (ifaces.Count == 0) { Log.Error("No usable IPv4 interfaces. Set [server] bind= to an address on this machine."); return 1; }
Log.Info("Interfaces: " + string.Join(", ", ifaces.Select(i => i.Ip)));

// Firewall: whatever started us (service, console, GUI), make sure clients can get in.
if (cfg.Firewall && OperatingSystem.IsWindows())
{
    var fw = Firewall.Ensure(Environment.ProcessPath!, out var fwMessage);
    if (fw is Firewall.State.Ok or Firewall.State.Created) Log.Info($"Firewall: {fwMessage}");
    else Log.Warn($"Firewall: {fwMessage}");
}

// Registers our services with the dependency injection container. Used by both host variants below.
void AddServices(IServiceCollection s)
{
    s.AddSingleton(cfg);
    s.AddSingleton<BootPolicy>();
    s.AddSingleton<FileProvider>();
    s.AddHostedService<DhcpServer>();
    s.AddHostedService<TftpServer>();
    s.AddWindowsService(o => o.ServiceName = ServiceName);
}

// Two ways to build the host. With HTTP on we need a WebApplication (the generic host plus
// Kestrel). With HTTP off, a plain generic host avoids opening a web port at all.
try
{
    if (cfg.Http)
    {
        // CreateSlimBuilder = the AOT-friendly WebApplication: no IIS integration, no HTTPS
        // setup, no reflection-based routing. ContentRootPath is set to the exe's folder
        // because a service otherwise starts in C:\Windows\System32.
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory });
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        HttpFileServer.ConfigureListeners(builder.WebHost, cfg);
        AddServices(builder.Services);
        var app = builder.Build();
        HttpFileServer.Map(app, cfg, app.Services.GetRequiredService<FileProvider>());
        app.Lifetime.ApplicationStarted.Register(() =>
            Log.Info($"HTTP: listening on port {cfg.HttpPort}"));
        await app.RunAsync();
    }
    else
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = args, ContentRootPath = AppContext.BaseDirectory });
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        AddServices(builder.Services);
        await builder.Build().RunAsync();
    }
}
catch (IOException ex) when (ex.InnerException is System.Net.Sockets.SocketException)
{
    Log.Error($"HTTP: cannot listen on port {cfg.HttpPort}: {ex.Message} (is IIS or another web server using it?)");
    return 1;
}
return 0;

// ---- Service installation -------------------------------------------------------------
// Uses the built-in sc.exe tool rather than Windows service APIs: the commands are easy to
// read, and you can run them by hand to see exactly what --install does. Firewall.cs does the
// same with netsh.

static int InstallService(string configPath)
{
    var exe = Environment.ProcessPath!;
    var bin = $"\"{exe}\" --config \"{configPath}\"";
    int rc = Run("sc.exe", $"create {ServiceName} binPath= \"{bin.Replace("\"", "\\\"")}\" start= auto DisplayName= \"SharpPXE boot server\"");
    if (rc != 0) return rc;
    Run("sc.exe", $"description {ServiceName} \"proxyDHCP/DHCP, TFTP and HTTP network boot server\"");
    Run("sc.exe", $"failure {ServiceName} reset= 86400 actions= restart/5000/restart/5000/restart/30000");
    Firewall.Ensure(exe, out var fwMessage);
    Console.WriteLine($"Firewall: {fwMessage}");
    Console.WriteLine($"Installed. Start it with:  sc.exe start {ServiceName}");
    return 0;
}

static int UninstallService()
{
    Run("sc.exe", $"stop {ServiceName}");
    Firewall.Remove();
    return Run("sc.exe", $"delete {ServiceName}");
}

static int Run(string file, string arguments)
{
    using var p = Process.Start(new ProcessStartInfo(file, arguments) { UseShellExecute = false })!;
    p.WaitForExit();
    return p.ExitCode;
}
