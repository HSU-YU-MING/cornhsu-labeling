namespace Cornhsu.Labeling;

/// <summary>Match mode for multi-label queries.</summary>
public enum LabelMatch
{
    /// <summary>Matches when any of the labels is present (OR). Names that do not exist do not affect the other labels' results.</summary>
    Any = 0,

    /// <summary>Matches only when all of the labels are present (AND). If any name does not exist, the result is necessarily empty.</summary>
    All = 1,
}
