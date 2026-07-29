namespace Batcomputer;

/// <summary>Restricts game reads to shipped pak containers, never installed author mods.</summary>
internal static class BaseGamePakSource
{
    public const SearchOption ShippedContainerSearchOption = System.IO.SearchOption.TopDirectoryOnly;
}
