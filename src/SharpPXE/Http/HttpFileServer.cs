// =====================================================================================
//  HttpFileServer.cs - serving boot files over HTTP
// =====================================================================================
//
//  Once iPXE is running, everything else can come over HTTP instead of TFTP. HTTP runs on
//  TCP, which handles windows, retransmits and congestion itself, so a 500 MB boot.wim
//  loads at close to wire speed instead of the few MB/s typical of TFTP.
//
//  What the boot clients need from HTTP is small:
//    - GET and HEAD for files
//    - Range requests ("Range: bytes=1000-1999" -> 206 Partial Content), which iPXE and
//      UEFI HTTP Boot use to resume or read part of a file
//    - correct Content-Length
//
//  We use Kestrel, the web server built into ASP.NET Core. In Native AOT mode it's set up
//  with WebApplication.CreateSlimBuilder (Program.cs), which leaves out features that need
//  runtime reflection. Instead of MVC controllers or minimal-API routing, there's a single
//  "terminal middleware" (app.Run) that handles every request. That's all a file server needs.
// =====================================================================================

using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace SharpPXE.Http;

/// <summary>Static file server for iPXE, wimboot and UEFI HTTP Boot (Kestrel).</summary>
public static class HttpFileServer
{
    /// <summary>Tells Kestrel which addresses and port to listen on.</summary>
    public static void ConfigureListeners(IWebHostBuilder web, Config cfg)
    {
        web.ConfigureKestrel(k =>
        {
            k.AddServerHeader = false;          // no "Server: Kestrel" header
            k.Limits.MaxRequestBodySize = 0;    // we never accept uploads
            // Boot firmware speaks HTTP/1.1 only, so don't offer HTTP/2.
            if (cfg.Bind.Count == 0)
                k.Listen(IPAddress.Any, cfg.HttpPort, l => l.Protocols = HttpProtocols.Http1);
            else
                foreach (var (ip, _) in cfg.ResolveInterfaces())
                    k.Listen(ip, cfg.HttpPort, l => l.Protocols = HttpProtocols.Http1);
        });
    }

    /// <summary>Installs the request handler.</summary>
    public static void Map(WebApplication app, Config cfg, FileProvider files)
    {
        app.Run(async ctx =>
        {
            var req = ctx.Request;
            var remote = ctx.Connection.RemoteIpAddress?.MapToIPv4();
            if (!HttpMethods.IsGet(req.Method) && !HttpMethods.IsHead(req.Method))
            {
                ctx.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
                return;
            }

            // The URL path maps straight onto the root folder: /winpe/sources/boot.wim ->
            // <root>\winpe\sources\boot.wim. FileProvider rejects anything outside the root.
            var path = req.Path.Value ?? "/";
            var full = files.Resolve(path);
            if (full is null)
            {
                Log.Warn($"HTTP {remote}: 404 {path}");
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            // The local address of THIS connection, i.e. the IP the client used to reach us.
            // It replaces {server} in scripts, so links in the script point back the same way.
            var serverIp = ctx.Connection.LocalIpAddress?.MapToIPv4().ToString() ?? "127.0.0.1";

            if (FileProvider.IsTemplate(full))
            {
                // iPXE scripts are generated per request (placeholders filled in), so we write
                // them ourselves and tell the client not to cache them.
                await using var s = files.Open(full, serverIp, cfg.HttpPort);
                ctx.Response.ContentType = "text/plain; charset=utf-8";
                ctx.Response.ContentLength = s.Length;
                ctx.Response.Headers.CacheControl = "no-store";
                Log.Info($"HTTP {remote}: {req.Method} {path} (script, {s.Length:N0} bytes)");
                if (HttpMethods.IsGet(req.Method)) await s.CopyToAsync(ctx.Response.Body, ctx.RequestAborted);
                return;
            }

            // Everything else is a plain file. PhysicalFile handles HEAD, Range / 206 responses,
            // If-Modified-Since and Content-Length, and sends the file efficiently.
            var len = new FileInfo(full).Length;
            var range = req.Headers.Range.Count > 0 ? $" range {req.Headers.Range}" : "";
            Log.Info($"HTTP {remote}: {req.Method} {path} ({len:N0} bytes){range}");
            await TypedResults.PhysicalFile(full, "application/octet-stream", enableRangeProcessing: true).ExecuteAsync(ctx);
        });
    }
}
