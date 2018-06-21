using System;
using System.Net.Http;
using System.Security.Principal;
using System.Threading.Tasks;

namespace SharpReverseProxy {
    public class ProxyRule {
        public Func<Uri, bool> Matcher { get; set; } = uri => false;
        public Action<HttpRequestMessage, IPrincipal> Modifier { get; set; } = (msg, user) => { };
        public Func<HttpResponseMessage, Task> ResponseModifier { get; set; } = null;
        public bool PreProcessResponse { get; set; } = true;
        public bool RequiresAuthentication { get; set; }
    }
}
