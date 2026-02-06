using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

/// <summary>
/// SDP manipulation utilities.
/// Ported from daydream-browser WHIPClient.ts preferH264() pattern.
/// </summary>
public static class DaydreamSdpUtils
{
    private static readonly Regex H264RtpMapRegex = new Regex(@"a=rtpmap:(\d+) H264/\d+");

    /// <summary>
    /// Reorders m=video payload types to prioritize H.264.
    /// This ensures the Livepeer gateway selects H.264 for encoding.
    /// </summary>
    public static string PreferH264(string sdp)
    {
        if (string.IsNullOrEmpty(sdp)) return sdp;

        var lines = sdp.Split("\r\n");

        int mLineIdx = -1;
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].StartsWith("m=video"))
            {
                mLineIdx = i;
                break;
            }
        }

        if (mLineIdx == -1) return sdp;

        // Find all H.264 payload type numbers
        var h264Payloads = new List<string>();
        foreach (var line in lines)
        {
            var match = H264RtpMapRegex.Match(line);
            if (match.Success)
            {
                h264Payloads.Add(match.Groups[1].Value);
            }
        }

        if (h264Payloads.Count == 0) return sdp;

        // Reorder m=video line: put H.264 payloads first
        var parts = lines[mLineIdx].Split(' ');
        if (parts.Length < 4) return sdp;

        // m=video <port> <proto> <payload1> <payload2> ...
        var header = parts.Take(3);
        var payloads = parts.Skip(3).ToList();

        var reordered = h264Payloads
            .Where(p => payloads.Contains(p))
            .Concat(payloads.Where(p => !h264Payloads.Contains(p)))
            .ToArray();

        lines[mLineIdx] = string.Join(" ", header.Concat(reordered));

        return string.Join("\r\n", lines);
    }
}
