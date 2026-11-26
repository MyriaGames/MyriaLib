using System.Net.Security;
using System.Text.Json;

namespace Myria.Lib.Core.Utils
{
    /// <summary>
    /// Trust-on-first-use (TOFU) store for self-signed server certificates, keyed by
    /// "host:port". Lets a client connect to ANY operator-run server address - not just one
    /// hardcoded official server - while still detecting a certificate that changes after the
    /// first successful connection (a cert rotation the player should be warned about, or a
    /// possible MITM), the same trust model SSH uses for host keys.
    ///
    /// TOFU is only a fallback for servers that never had a publicly trusted certificate. A host
    /// that ever presented one is remembered as CA-validated and never falls back to TOFU again,
    /// and a certificate for a different name is never accepted (security review 2026-11-26,
    /// A04 F1: otherwise anyone on the player's network could present their own certificate for
    /// the official server - which the store had never recorded - and read password and token).
    /// </summary>
    public static class TrustedServerCertStore
    {
        /// <summary>Stored instead of a thumbprint for hosts whose certificate validated normally.</summary>
        public const string CaValidated = "ca-validated";

        private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
        private static readonly object Gate = new();
        private static Dictionary<string, string>? _cache;

        // Settable for tests only.
        internal static string PathFile { get; set; } = Path.Combine("Data", "Misc", "trusted_certs.json");

        internal static void ResetCache()
        {
            lock (Gate) _cache = null;
        }

        /// <summary>
        /// The whole certificate decision for a client's HttpClientHandler callback.
        /// <paramref name="authority"/> is "host:port" (the store key), <paramref name="host"/> the bare
        /// host name. <paramref name="caOnlyHosts"/> are hosts known to have a publicly trusted
        /// certificate (the official server) - they never use TOFU, not even on the very first start.
        /// </summary>
        public static bool IsAcceptable(string authority, string host, string? thumbprint, SslPolicyErrors errors,
            IEnumerable<string> caOnlyHosts)
        {
            if (errors == SslPolicyErrors.None)
            {
                RememberCaValidated(authority);
                return true;
            }
            if (caOnlyHosts.Any(h => string.Equals(h, host, StringComparison.OrdinalIgnoreCase)))
                return false;
            // A self-hosted server's self-signed certificate fails only the chain (untrusted root). A
            // missing certificate or one issued for another name is never acceptable.
            if ((errors & (SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateNotAvailable)) != 0)
                return false;
            if (string.IsNullOrEmpty(thumbprint))
                return false;
            return TrustOnFirstUse(authority, thumbprint);
        }

        /// <summary>
        /// Returns true if <paramref name="thumbprint"/> should be trusted for <paramref name="host"/>:
        /// either it matches what was already trusted for this host, or nothing was trusted for
        /// this host yet (first connection - trust and remember it). Returns false if this host
        /// already has a *different* trusted thumbprint on file, or once presented a publicly
        /// trusted certificate.
        /// </summary>
        public static bool TrustOnFirstUse(string host, string thumbprint)
        {
            lock (Gate)
            {
                var store = Load();
                if (store.TryGetValue(host, out var trusted))
                    return trusted != CaValidated &&
                           string.Equals(trusted, thumbprint, StringComparison.OrdinalIgnoreCase);

                store[host] = thumbprint;
                Save(store);
                return true;
            }
        }

        /// <summary>Marks a host as having a publicly trusted certificate: no TOFU for it from now on.</summary>
        public static void RememberCaValidated(string host)
        {
            lock (Gate)
            {
                var store = Load();
                if (store.TryGetValue(host, out var current) && current == CaValidated)
                    return;
                store[host] = CaValidated;
                Save(store);
            }
        }

        /// <summary>Clears a previously-trusted certificate for a host, e.g. after a legitimate
        /// cert rotation the player has confirmed out-of-band - the next connection will
        /// trust-on-first-use again.</summary>
        public static void Forget(string host)
        {
            lock (Gate)
            {
                var store = Load();
                if (store.Remove(host))
                    Save(store);
            }
        }

        private static Dictionary<string, string> Load()
        {
            if (_cache is not null) return _cache;

            try
            {
                if (File.Exists(PathFile))
                {
                    var json = File.ReadAllText(PathFile);
                    _cache = JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new();
                    return _cache;
                }
            }
            catch
            {
                // Corrupt/unreadable store - fall through to a fresh one rather than blocking
                // every connection on a local-file problem.
            }

            _cache = new Dictionary<string, string>();
            return _cache;
        }

        private static void Save(Dictionary<string, string> store)
        {
            _cache = store;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(PathFile)!);
                File.WriteAllText(PathFile, JsonSerializer.Serialize(store, JsonOpts));
            }
            catch
            {
                // Can't persist (read-only folder etc.) - the decision still holds for this session.
            }
        }
    }
}
