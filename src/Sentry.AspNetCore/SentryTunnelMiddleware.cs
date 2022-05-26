using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Sentry.AspNetCore;

/// <summary>
/// Middleware that can forward Sentry envelopes.
/// </summary>
/// <seealso href="https://docs.sentry.io/platforms/javascript/troubleshooting/#dealing-with-ad-blockers"/>
public class SentryTunnelMiddleware : IMiddleware
{
    private readonly string[] _allowedHosts;
    private Lazy<string> Version => new(() => (GetType().Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? string.Empty));

    /// <summary>
    /// Middleware that can forward Sentry envelopes.
    /// </summary>
    /// <seealso href="https://docs.sentry.io/platforms/javascript/troubleshooting/#dealing-with-ad-blockers"/>
    public SentryTunnelMiddleware(string[] allowedHosts)
    {
        _allowedHosts = new[] { "sentry.io" }.Concat(allowedHosts).ToArray();
    }

    /// <inheritdoc />
    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        var response = context.Response;
        var headers = response.Headers;
        var request = context.Request;
        if (request.Method == "OPTIONS")
        {
            headers.Add("Access-Control-Allow-Origin", new[] { (string)request.Headers["Origin"] });
            headers.Add("Access-Control-Allow-Headers", new[] { "Origin, X-Requested-With, Content-Type, Accept" });
            headers.Add("Access-Control-Allow-Methods", new[] { "POST, OPTIONS" });
            headers.Add("Access-Control-Allow-Credentials", new[] { "true" });
            response.StatusCode = 200;
            return;
        }

        var httpClientFactory = context.RequestServices.GetRequiredService<IHttpClientFactory>();
        var client = httpClientFactory.CreateClient("SentryTunnel");
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Sentry.NET_Tunnel", Version.Value));
        var memoryStream = new MemoryStream();
        await request.Body.CopyToAsync(memoryStream).ConfigureAwait(false);
        memoryStream.Position = 0;
        using var reader = new StreamReader(memoryStream);
        var header = await reader.ReadLineAsync().ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(header))
        {
            response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        try
        {
            var headerJson = JsonSerializer.Deserialize<Dictionary<string, object>>(header);
            if (headerJson == null)
            {
                response.StatusCode = StatusCodes.Status400BadRequest;
                await response.WriteAsync("Invalid DSN JSON supplied").ConfigureAwait(false);
                return;
            }
            if (headerJson.TryGetValue("dsn", out var dsnString) && Uri.TryCreate(dsnString.ToString(), UriKind.Absolute, out var dsn) && _allowedHosts.Contains(dsn.Host))
            {
                var projectId = dsn.AbsolutePath.Trim('/');
                memoryStream.Position = 0;
                var sentryRequest = new HttpRequestMessage
                {
                    RequestUri = new Uri($"https://{dsn.Host}/api/{projectId}/envelope/"),
                    Method = HttpMethod.Post,
                    Content = new StreamContent(memoryStream),
                };
                var clientIp = context.Connection?.RemoteIpAddress?.ToString();
                if (clientIp != null)
                {
                    sentryRequest.Headers.Add("X-Forwarded-For", context.Connection?.RemoteIpAddress?.ToString());
                }
                var responseMessage = await client.SendAsync(sentryRequest).ConfigureAwait(false);
                // We send the response back to the client, whatever it was
                headers["content-type"] = "application/json";
                response.StatusCode = (int)responseMessage.StatusCode;
                await responseMessage.Content.CopyToAsync(response.Body).ConfigureAwait(false);
            }
        }
        catch (JsonException)
        {
            response.StatusCode = StatusCodes.Status400BadRequest;
            await response.WriteAsync("Invalid DSN JSON supplied").ConfigureAwait(false);
        }
        catch (ArgumentNullException)
        {
            response.StatusCode = StatusCodes.Status400BadRequest;
            await response.WriteAsync("Received empty body").ConfigureAwait(false);
        }
    }
}