#if HAS_NEXUS
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Hd2Mod
{
    /// <summary>Signing in to Nexus Mods with OAuth 2.0 and PKCE, as Nexus Mods asks of a public app
    /// such as a mod manager (their reply, 2026-10-27, and modding.wiki/en/api/oauth2-guide): the
    /// player allows this program on Nexus Mods' own page, in their browser; the code comes back to
    /// a listener on this computer only (127.0.0.1); it is exchanged for an access token, which every
    /// API request then carries ("Authorization: Bearer"), and a refresh token that renews it. No
    /// password and no secret: a public app has none, and PKCE makes a code that was caught on its
    /// way useless without the verifier this program keeps. The tokens are kept on this computer
    /// only, encrypted for this Windows user.</summary>
    public static class NexusOAuth
    {
        /// <summary>The client id Nexus Mods issued for this build's application, which the build
        /// takes from nexus_client_id.txt beside build.bat (not in the repository: a build anyone else
        /// makes registers its own application with Nexus Mods). Null without one: no sign-in.
        /// Nothing at run time can give it another (no setting, no environment variable): a build
        /// without one cannot sign in (the user, 2026-10-27).</summary>
        public static string ClientId => ForTests ?? Built;

        /// <summary>Tests only: a client id of the fake sign-in service's.</summary>
        internal static string ForTests;

        static readonly string Built = typeof(NexusOAuth).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
            .OfType<System.Reflection.AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "NexusClientId")?.Value is string v && v.Length > 0 ? v : null;

        /// <summary>Where the browser brings the code back: this computer only. The port is part of
        /// the address registered with Nexus Mods, so it does not change; tests use another.</summary>
        public static int Port = 51787;

        public static string RedirectUri => "http://127.0.0.1:" + Port + "/callback";

        /// <summary>The time, in Unix seconds; tests move it on to see a token renewed.</summary>
        public static Func<long> Now = () => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        /// <summary>Nexus Mods' sign-in service; tests point it at a local fake.</summary>
        public static string UsersBase = Environment.GetEnvironmentVariable("HD2QD_NEXUS_USERS") ?? "https://users.nexusmods.com";

        /// <summary>The key Nexus Mods signs its tokens with (published in its guide); tests use their own.</summary>
        public static string PublicKeyPem =
            "MIIBIjANBgkqhkiG9w0BAQEFAAOCAQ8AMIIBCgKCAQEAs/57oX8HW8xC+W/etH7J" +
            "PgoSTiGPKZa6Gq3/K/7GgrpJcZhPdr9MTGocb2uLzQBJW+u1XpSgyeKH4JCxxeHF" +
            "3zcUtb7SUg3KnxlR5QUmOnqBvbUuL4opUpfgWUGltASduYqZBJD2WTK8Hvwh9X1v" +
            "ACeqp1zgorZm3f0J2H15TDbzIp9ihCFuthJUFumdzvrt/WvimW2fiyqndTNQwe5h" +
            "XM8hj8cemdWQXCd99qnj7UQkpu+yNisVMHQCsAqXITe6Ehp6IY9eCd4DJKjDvyLc" +
            "3vbY8UL+bcVK5tYAKemZ56uw3q1YdcyqGlItyLi4j4EISdBQaCCqT7YZUhMYzhUd" +
            "1QIDAQAB";

        // ------------------------------------------------------------ PKCE

        /// <summary>A verifier: 43 random bytes as hex, 86 characters (RFC 7636 asks 43 to 128).</summary>
        public static string NewVerifier()
        {
            var b = new byte[43];
            using (var r = RandomNumberGenerator.Create()) r.GetBytes(b);
            return string.Concat(b.Select(x => x.ToString("x2")));
        }

        /// <summary>The challenge (S256): the verifier's SHA-256, base64url without padding.</summary>
        public static string Challenge(string verifier)
        {
            using (var sha = SHA256.Create())
                return Base64Url(sha.ComputeHash(Encoding.ASCII.GetBytes(verifier)));
        }

        static string Base64Url(byte[] b) => Convert.ToBase64String(b).Replace('+', '-').Replace('/', '_').TrimEnd('=');

        static byte[] FromBase64Url(string s)
        {
            s = s.Replace('-', '+').Replace('_', '/');
            return Convert.FromBase64String(s + new string('=', (4 - s.Length % 4) % 4));
        }

        public static string AuthorizeUrl(string challenge, string state) =>
            UsersBase.TrimEnd('/') + "/oauth/authorize?" + string.Join("&", new[]
            {
                "client_id=" + Uri.EscapeDataString(ClientId ?? ""), "response_type=code", "scope=",
                "redirect_uri=" + Uri.EscapeDataString(RedirectUri), "state=" + Uri.EscapeDataString(state),
                "code_challenge_method=S256", "code_challenge=" + Uri.EscapeDataString(challenge),
            });

        // ------------------------------------------------------------ signing in

        /// <summary>The whole sign-in: the browser opened on Nexus Mods' page (open), the code taken
        /// from the one request that comes back to 127.0.0.1 within the time, exchanged for tokens,
        /// which are checked and kept. The account signed in, as Account says it.</summary>
        public static (string Name, bool Premium) Login(Action<string> open, TimeSpan wait)
        {
            if (ClientId == null) throw new ModError("Nexus Mods has not issued this program's sign-in id yet", "nexus_no_client");
            var verifier = NewVerifier();
            var state = Guid.NewGuid().ToString("N");
            var listener = new TcpListener(IPAddress.Loopback, Port);
            try { listener.Start(); }
            catch (SocketException e) { throw new ModError("port " + Port + " on this computer is taken, so Nexus Mods cannot send the sign-in back: " + e.Message, "nexus_port"); }
            string code;
            try
            {
                open(AuthorizeUrl(Challenge(verifier), state));
                code = WaitForCode(listener, state, DateTime.UtcNow + wait);
            }
            finally { listener.Stop(); }
            Keep(Token(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code", ["redirect_uri"] = RedirectUri, ["scope"] = "",
                ["client_id"] = ClientId, ["code"] = code, ["code_verifier"] = verifier,
            }));
            return Account();
        }

        /// <summary>Requests to the listener until the one with this sign-in's state: its code. Any other
        /// path (a browser asks for favicon.ico) is answered 404 and waited past.</summary>
        static string WaitForCode(TcpListener listener, string state, DateTime until)
        {
            while (DateTime.UtcNow < until)
            {
                if (!listener.Pending()) { System.Threading.Thread.Sleep(100); continue; }
                using (var c = listener.AcceptTcpClient())
                {
                    c.ReceiveTimeout = 5000;
                    var s = c.GetStream();
                    var line = new StreamReader(s, Encoding.ASCII).ReadLine() ?? "";       // GET /callback?code=..&state=.. HTTP/1.1
                    var parts = line.Split(' ');
                    var target = parts.Length > 1 ? parts[1] : "";
                    var q = target.IndexOf('?');
                    var path = q < 0 ? target : target.Substring(0, q);
                    var args = Query(q < 0 ? "" : target.Substring(q + 1));
                    if (path != "/callback" || !args.TryGetValue("state", out var st) || st != state)
                    {
                        Answer(s, 404, "");
                        continue;
                    }
                    if (args.TryGetValue("error", out var err))
                    {
                        Answer(s, 200, Page(false));
                        throw new ModError("Nexus Mods did not allow the sign-in: " + err, "nexus_denied");
                    }
                    if (!args.TryGetValue("code", out var code) || code.Length == 0) { Answer(s, 400, Page(false)); continue; }
                    Answer(s, 200, Page(true));
                    return code;
                }
            }
            throw new ModError("no sign-in came back from Nexus Mods in time", "nexus_timeout");
        }

        static Dictionary<string, string> Query(string q) =>
            q.Split('&').Where(p => p.Length > 0).Select(p => p.Split(new[] { '=' }, 2))
             .GroupBy(p => Uri.UnescapeDataString(p[0]))
             .ToDictionary(g => g.Key, g => Uri.UnescapeDataString((g.First().Length > 1 ? g.First()[1] : "").Replace('+', ' ')));

        static string Page(bool ok) =>
            "<!doctype html><meta charset=\"utf-8\"><title>HD2 Mod Quick Deploy</title>" +
            "<body style=\"font-family:sans-serif;background:#111;color:#eee;padding:40px\"><p>" +
            (ok ? "HD2 Mod Quick Deploy is signed in to Nexus Mods. This tab can be closed.<br>HD2 Mod Quick Deploy 已登录 Nexus Mods，可以关闭此页。"
                : "The sign-in did not complete. Return to HD2 Mod Quick Deploy.<br>登录未完成，请返回 HD2 Mod Quick Deploy。") + "</p></body>";

        static void Answer(Stream s, int status, string html)
        {
            var body = Encoding.UTF8.GetBytes(html);
            var head = Encoding.ASCII.GetBytes("HTTP/1.1 " + status + (status == 200 ? " OK" : " Not Found") + "\r\nContent-Type: text/html; charset=utf-8\r\n" +
                                               "Content-Length: " + body.Length + "\r\nConnection: close\r\n\r\n");
            s.Write(head, 0, head.Length);
            s.Write(body, 0, body.Length);
        }

        /// <summary>A request to the token endpoint: the tokens it answers with.</summary>
        static Tokens Token(Dictionary<string, string> form)
        {
            HttpResponseMessage r;
            try { r = Nexus.Http.PostAsync(UsersBase.TrimEnd('/') + "/oauth/token", new FormUrlEncodedContent(form)).GetAwaiter().GetResult(); }
            catch (HttpRequestException e) { throw new ModError("Nexus Mods cannot be reached: " + e.Message, "nexus_offline"); }
            using (r)
            {
                var body = r.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                if ((int)r.StatusCode >= 400 && (int)r.StatusCode < 500)
                    throw new ModError("Nexus Mods refused the sign-in (" + (int)r.StatusCode + "): " + body, "nexus_login");
                if (!r.IsSuccessStatusCode) throw new ModError("Nexus Mods answered " + (int)r.StatusCode + ": " + body, "nexus_error");
                var d = Json.Parse(body) as Dictionary<string, object>;
                var t = new Tokens
                {
                    Access = Json.Str(d, "access_token"),
                    Refresh = Json.Str(d, "refresh_token") ?? (form.TryGetValue("refresh_token", out var kept) ? kept : null),
                    ExpiresAt = Now() + Math.Max(60, Json.Long(d, "expires_in")),
                };
                if (string.IsNullOrEmpty(t.Access)) throw new ModError("Nexus Mods gave no access token", "nexus_error");
                Verify(t.Access);
                return t;
            }
        }

        // ------------------------------------------------------------ the tokens

        public class Tokens
        {
            public string Access, Refresh;
            public long ExpiresAt;
        }

        static readonly byte[] Salt = Encoding.UTF8.GetBytes("hd2qd nexus oauth");

        /// <summary>The tokens, encrypted for this Windows user (DPAPI), in nexus.json.</summary>
        static void Keep(Tokens t)
        {
            var d = Store.Load("nexus.json");
            if (t == null) d.Remove("oauth");
            else
            {
                var plain = Json.Write(new Dictionary<string, object> { ["access"] = t.Access, ["refresh"] = t.Refresh, ["expires_at"] = t.ExpiresAt });
                d["oauth"] = Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Salt, DataProtectionScope.CurrentUser));
            }
            Store.Save("nexus.json", d);
        }

        static Tokens Kept()
        {
            var s = Json.Str(Store.Load("nexus.json"), "oauth");
            if (string.IsNullOrEmpty(s)) return null;
            try
            {
                var d = Json.Parse(Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(s), Salt, DataProtectionScope.CurrentUser))) as Dictionary<string, object>;
                return new Tokens { Access = Json.Str(d, "access"), Refresh = Json.Str(d, "refresh"), ExpiresAt = Json.Long(d, "expires_at") };
            }
            catch (Exception e) when (e is CryptographicException || e is FormatException) { return null; }
        }

        public static bool SignedIn => Kept() != null;

        /// <summary>Signed out here: the tokens forgotten. The access stays allowed on Nexus Mods
        /// until the player takes it back there.</summary>
        public static void Logout() => Keep(null);

        /// <summary>What a request carries ("Bearer ..."), renewed first when it ends within a
        /// minute; null when not signed in. A renewal Nexus Mods refuses means the player took the
        /// access back there: signed out here too.</summary>
        public static string Credential()
        {
            var t = Kept();
            if (t == null) return null;
            if (t.ExpiresAt - Now() < 60)
            {
                if (string.IsNullOrEmpty(t.Refresh) || ClientId == null) { Logout(); throw new ModError("the Nexus Mods sign-in has ended: sign in again", "nexus_login"); }
                try
                {
                    t = Token(new Dictionary<string, string> { ["grant_type"] = "refresh_token", ["client_id"] = ClientId, ["refresh_token"] = t.Refresh });
                }
                catch (ModError e) when (e.Kind == "nexus_login")
                {
                    Logout();
                    throw new ModError("Nexus Mods ended this program's access: sign in again", "nexus_login");
                }
                Keep(t);
            }
            return "Bearer " + t.Access;
        }

        /// <summary>The account signed in, from the access token itself: its name, and premium.</summary>
        public static (string Name, bool Premium) Account()
        {
            var t = Kept() ?? throw new ModError("not signed in to Nexus Mods", "nexus_login");
            var user = Json.Obj(Payload(t.Access), "user");
            var roles = Json.List(user, "membership_roles").OfType<string>().ToList();
            return (Json.Str(user, "username") ?? "?", roles.Contains("premium") || roles.Contains("lifetimepremium"));
        }

        static Dictionary<string, object> Payload(string jwt)
        {
            var parts = jwt.Split('.');
            if (parts.Length != 3) throw new ModError("the Nexus Mods token is not one", "nexus_error");
            return Json.Parse(Encoding.UTF8.GetString(FromBase64Url(parts[1]))) as Dictionary<string, object>;
        }

        /// <summary>The token's signature checked with Nexus Mods' public key (RS256), as their guide
        /// asks: a token that does not verify is never kept.</summary>
        public static void Verify(string jwt)
        {
            var parts = jwt.Split('.');
            if (parts.Length != 3) throw new ModError("the Nexus Mods token is not one", "nexus_error");
            var head = Json.Parse(Encoding.UTF8.GetString(FromBase64Url(parts[0]))) as Dictionary<string, object>;
            if (Json.Str(head, "alg") != "RS256") throw new ModError("the Nexus Mods token is signed with " + Json.Str(head, "alg") + ", not RS256", "nexus_error");
            using (var rsa = new RSACryptoServiceProvider())
            {
                rsa.ImportParameters(KeyOf(PublicKeyPem));
                if (!rsa.VerifyData(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), CryptoConfig.MapNameToOID("SHA256"), FromBase64Url(parts[2])))
                    throw new ModError("the Nexus Mods token's signature does not verify", "nexus_error");
            }
        }

        /// <summary>A public key (SubjectPublicKeyInfo, base64 without its PEM lines): modulus and exponent.</summary>
        public static RSAParameters KeyOf(string base64)
        {
            var der = Convert.FromBase64String(string.Concat(base64.Where(ch => !char.IsWhiteSpace(ch))));
            int at = 0;
            int Header(byte tag)
            {
                if (der[at++] != tag) throw new ModError("the Nexus Mods key cannot be read", "nexus_error");
                int len = der[at++];
                if (len > 0x80) { int n = len - 0x80; len = 0; for (int i = 0; i < n; i++) len = len << 8 | der[at++]; }
                return len;
            }
            byte[] Value(byte tag)
            {
                int len = Header(tag);
                var v = new byte[len];
                Array.Copy(der, at, v, 0, len);
                at += len;
                return v;
            }
            Header(0x30);           // SubjectPublicKeyInfo
            Value(0x30);            // its algorithm: RSA, passed over
            Header(0x03); at++;     // the key, a bit string: past its unused-bits byte
            Header(0x30);           // RSAPublicKey
            var modulus = Value(0x02);
            var exponent = Value(0x02);
            return new RSAParameters { Modulus = modulus.SkipWhile(b => b == 0).ToArray(), Exponent = exponent };
        }
    }
}
#endif
