#nullable enable
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace CamoConnector.Detector;

public sealed record CaptureDevice(string Name, string Input);

public static class CaptureDevices
{
    public static CaptureDevice[] Parse(string listing)
    {
        var devices = new List<CaptureDevice>();
        var current = -1;
        foreach (var line in listing.Split('\n'))
        {
            var name = Regex.Match(line, @"\x22([^\x22]+)\x22\s*\((video|none|audio)\)");
            if (name.Success)
            {
                current = -1;
                if (name.Groups[2].Value == "audio") continue;
                var label = name.Groups[1].Value;
                current = devices.Count;
                devices.Add(new(label, label));
                continue;
            }
            var alternative = Regex.Match(line, @"Alternative name\s*\x22([^\x22]+)\x22");
            if (current >= 0 && alternative.Success)
            {
                devices[current] = devices[current] with { Input = alternative.Groups[1].Value };
                current = -1;
            }
        }
        return devices.ToArray();
    }
}
