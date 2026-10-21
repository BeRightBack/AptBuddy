namespace AptBuddy.ViewModels;

/// <summary>UI-friendly view of one dependency row (avoids converters in XAML).</summary>
public sealed record DepRow(string Kind, string Package, string Status);

/// <summary>UI-friendly view of one repository source line.</summary>
public sealed record RepoRow(string Status, string Uri, string Suite, string Component, string SignedBy, string File, string Format);