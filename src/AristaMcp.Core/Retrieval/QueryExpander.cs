using System.Text;
using System.Text.RegularExpressions;

namespace AristaMcp.Core.Retrieval;

// Annotates acronyms common in Arista documentation on first occurrence:
//   "EVPN overlay" → "EVPN (Ethernet VPN) overlay"
// Case-insensitive match; the acronym's original casing is preserved. Each acronym
// is expanded at most once per query so repeated mentions don't bloat the embedding.
public static partial class QueryExpander
{
    // Order matters only for documentation — matching is per-word on any occurrence.
    private static readonly Dictionary<string, string> Synonyms =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["EVPN"] = "Ethernet VPN",
            ["VXLAN"] = "Virtual Extensible LAN",
            ["MLAG"] = "Multi-chassis Link Aggregation",
            ["EOS"] = "Arista Extensible Operating System",
            ["CVP"] = "CloudVision Portal",
            ["DMF"] = "Arista DANZ Monitoring Fabric",
            ["BGP"] = "Border Gateway Protocol",
            ["OSPF"] = "Open Shortest Path First",
            ["LACP"] = "Link Aggregation Control Protocol",
            ["sFlow"] = "sampled flow",
            ["SR"] = "Segment Routing",
            ["MSS"] = "Multi-domain Segment Security",
            ["AVD"] = "Arista Validated Designs",
            ["LANZ"] = "Latency Analyzer",
            ["VARP"] = "Virtual ARP",
            ["VRRP"] = "Virtual Router Redundancy Protocol",
            ["VRF"] = "Virtual Routing and Forwarding",
            ["QoS"] = "Quality of Service",
            ["ACL"] = "Access Control List",
            ["TCAM"] = "Ternary Content-Addressable Memory",
        };

    [GeneratedRegex(@"\b[\w][\w\-]*\b", RegexOptions.ExplicitCapture)]
    private static partial Regex WordRegex();

    public static QueryExpansion Expand(string query)
    {
        ArgumentNullException.ThrowIfNull(query);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sb = new StringBuilder(query.Length + 32);
        var lastIndex = 0;

        foreach (Match m in WordRegex().Matches(query))
        {
            if (m.Index > lastIndex)
            {
                sb.Append(query, lastIndex, m.Index - lastIndex);
            }

            var token = m.Value;
            sb.Append(token);

            if (Synonyms.TryGetValue(token, out var expansion) && seen.Add(token))
            {
                sb.Append(" (").Append(expansion).Append(')');
            }

            lastIndex = m.Index + m.Length;
        }

        if (lastIndex < query.Length)
        {
            sb.Append(query, lastIndex, query.Length - lastIndex);
        }

        var expanded = sb.ToString();
        return new QueryExpansion(query, expanded);
    }
}
