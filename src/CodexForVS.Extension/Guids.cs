using System;

namespace CodexForVS.Extension;

internal static class Guids
{
    public const string PackageString = "E4B5CB36-C9C0-4D80-9DC4-73E4C67AC2D6";
    public const string CommandSetString = "96C54587-1A5A-4E2E-A9A4-432B88EB5D35";

    public static readonly Guid CommandSet = new(CommandSetString);
}
