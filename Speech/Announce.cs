namespace PokemonTCGLiveAccessibility.Speech;

internal static class Announce
{
    public static string Item(string name, string? description, int index, int total)
    {
        var idx = $"{index} of {total}";
        return string.IsNullOrEmpty(description)
            ? $"{name}, {idx}"
            : $"{name}, {description}, {idx}";
    }

    public static string Option(string name, string? currentValue, int index, int total)
    {
        var idx = $"{index} of {total}";
        return string.IsNullOrEmpty(currentValue)
            ? $"{name}, {idx}"
            : $"{name}, {currentValue}, {idx}";
    }
}
