using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using System.Web;

namespace SharpReverseProxy {
    using AppFunc = Func<IDictionary<string, object>, Task>;
    public class ProxyMiddleware {

        // Headers not forwarded to the remote endpoint
        private static readonly HashSet<string> ExcludedRequestHeaders =
            new HashSet<string> {
                "connection",
                "content-length",
                "keep-alive",
                "upgrade",
                "upgrade-insecure-requests"
            };

        // Headers returned by the remote endpoint
        // which are not returned to the client
        private static readonly HashSet<string> ExcludedResponseHeaders =
            new HashSet<string> {
                "connection",
                "server",
                "transfer-encoding",
                "upgrade",
                "x-powered-by"
            };

        private readonly AppFunc _next;
        private readonly HttpClient _httpClient;
        private readonly ProxyOptions _options;

        public ProxyMiddleware(AppFunc next, ProxyOptions options) {
            _next = next;
            _options = options;
            _httpClient = new HttpClient(_options.BackChannelMessageHandler ?? new HttpClientHandler {
                AllowAutoRedirect = _options.FollowRedirects
            });
        }

        public async Task Invoke(IDictionary<string, object> environment) {
            var context = (HttpContextBase) environment["System.Web.HttpContextBase"];
            var uri = GeRequestUri(context);
            var resultBuilder = new ProxyResultBuilder(uri);

            var matchedRule = _options.ProxyRules.FirstOrDefault(r => r.Matcher.Invoke(uri));
            if (matchedRule == null) {
                await _next(environment);
                _options.Reporter.Invoke(resultBuilder.NotProxied(context.Response.StatusCode));
                return;
            }

            if (matchedRule.RequiresAuthentication && !UserIsAuthenticated(context)) {
                context.Response.StatusCode = (int) HttpStatusCode.Unauthorized;
                _options.Reporter.Invoke(resultBuilder.NotAuthenticated());
                return;
            }

            var proxyRequest = new HttpRequestMessage(new HttpMethod(context.Request.HttpMethod), uri);
            SetProxyRequestBody(proxyRequest, context);
            SetProxyRequestHeaders(proxyRequest, context);

            matchedRule.Modifier.Invoke(proxyRequest, context.User);

            proxyRequest.Headers.Host = !proxyRequest.RequestUri.IsDefaultPort 
                ? $"{proxyRequest.RequestUri.Host}:{proxyRequest.RequestUri.Port}"
                : proxyRequest.RequestUri.Host;

            try {
                await ProxyTheRequest(context, proxyRequest, matchedRule);
            }
            catch (HttpRequestException) {
                context.Response.StatusCode = (int) HttpStatusCode.ServiceUnavailable;
            }
            _options.Reporter.Invoke(resultBuilder.Proxied(proxyRequest.RequestUri, context.Response.StatusCode));
        }

        private async Task ProxyTheRequest(HttpContextBase context, HttpRequestMessage proxyRequest, ProxyRule proxyRule) {
            using (var responseMessage = await _httpClient.SendAsync(proxyRequest,
                                                                     HttpCompletionOption.ResponseHeadersRead)) {

                if(proxyRule.PreProcessResponse || proxyRule.ResponseModifier == null) {
                    context.Response.ClearHeaders();
                    context.Response.StatusCode = (int)responseMessage.StatusCode;
                    foreach (var header in responseMessage.Headers) {
                        if (ExcludedResponseHeaders.Contains(header.Key.ToLowerInvariant())) {
                            continue;
                        }
                        foreach (var value in header.Value.ToArray()) {
                            context.Response.AddHeader(header.Key, value);
                        }
                    }

                    if (responseMessage.Content != null) {
                        foreach (var contentHeader in responseMessage.Content.Headers) {
                            if (ExcludedResponseHeaders.Contains(contentHeader.Key.ToLowerInvariant())) {
                                continue;
                            }
                            foreach (var value in contentHeader.Value.ToArray()) {
                                context.Response.AddHeader(contentHeader.Key, value);
                            }
                        }
                        await responseMessage.Content.CopyToAsync(context.Response.OutputStream);
                    }
                }

                if (proxyRule.ResponseModifier != null) {
                    await proxyRule.ResponseModifier.Invoke(responseMessage);
                }
            }
        }

        private static Uri GeRequestUri(HttpContextBase context) {
            return context.Request.Url;
        }

        private static void SetProxyRequestBody(HttpRequestMessage requestMessage, HttpContextBase context) {
            var requestMethod = context.Request.HttpMethod;
            if (requestMethod == "GET" ||
                requestMethod == "HEAD" ||
                requestMethod == "DELETE" ||
                requestMethod == "TRACE") {
                return;
            }
            requestMessage.Content = new StreamContent(context.Request.InputStream);
        }

        private void SetProxyRequestHeaders(HttpRequestMessage requestMessage, HttpContextBase context) {
            foreach (string header in context.Request.Headers) {
                if (ExcludedRequestHeaders.Contains(header.ToLowerInvariant())) {
                    continue;
                }
                var values = context.Request.Headers.GetValues(header);
                if (!requestMessage.Headers.TryAddWithoutValidation(header, values)) {
                    requestMessage.Content?.Headers.TryAddWithoutValidation(header, values);
                }
            }
        }

        private bool UserIsAuthenticated(HttpContextBase context) {
            return context.User.Identity.IsAuthenticated;
        }
    }

}
