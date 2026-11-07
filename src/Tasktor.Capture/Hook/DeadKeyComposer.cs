using Tasktor.Contracts.Platform;

namespace Tasktor.Capture.Hook;

/// <summary>A Unicode candidate is distinct from uncertainty; default is always uncertain.</summary>
public enum DeadKeyCompositionKind : byte
{
    Uncertain = 0,
    Composed = 1,
}

public readonly record struct DeadKeyComposition(DeadKeyCompositionKind Kind, Sensitive<string>? Text);

/// <summary>
/// Conservative precomposed Unicode candidates for four spacing accents and ASCII letters. These tables contain
/// canonical NFC mappings, not a Windows layout table; the caller must independently establish the source keyboard
/// state and layout semantics. Unlisted sequences, including space and repeated dead keys, are uncertain. Explicit
/// tables are necessary because the application uses invariant globalization, where Normalize does not compose.
/// </summary>
public static class DeadKeyComposer
{
    public static DeadKeyComposition Compose(Sensitive<string> spacingAccent, Sensitive<string> baseText, bool sourceStateKnownClear)
    {
        if (!sourceStateKnownClear) return default;
        var accent = spacingAccent.Reveal();
        var text = baseText.Reveal();
        if (string.IsNullOrEmpty(text) || text.Length != 1) return default;
        var (letters, composed) = accent switch
        {
            "^" => ("aceghijosuwyzACEGHIJOSUWYZ", "\u00e2\u0109\u00ea\u011d\u0125\u00ee\u0135\u00f4\u015d\u00fb\u0175\u0177\u1e91\u00c2\u0108\u00ca\u011c\u0124\u00ce\u0134\u00d4\u015c\u00db\u0174\u0176\u1e90"),
            "¨" => ("aehiotuwxyAEHIOUWXY", "\u00e4\u00eb\u1e27\u00ef\u00f6\u1e97\u00fc\u1e85\u1e8d\u00ff\u00c4\u00cb\u1e26\u00cf\u00d6\u00dc\u1e84\u1e8c\u0178"),
            "´" => ("acegiklmnoprsuwyzACEGIKLMNOPRSUWYZ", "\u00e1\u0107\u00e9\u01f5\u00ed\u1e31\u013a\u1e3f\u0144\u00f3\u1e55\u0155\u015b\u00fa\u1e83\u00fd\u017a\u00c1\u0106\u00c9\u01f4\u00cd\u1e30\u0139\u1e3e\u0143\u00d3\u1e54\u0154\u015a\u00da\u1e82\u00dd\u0179"),
            "`" => ("aeinouwyAEINOUWY", "\u00e0\u00e8\u00ec\u01f9\u00f2\u00f9\u1e81\u1ef3\u00c0\u00c8\u00cc\u01f8\u00d2\u00d9\u1e80\u1ef2"),
            _ => ("", ""),
        };
        var index = letters.IndexOf(text[0]);
        return index < 0 ? default : new(DeadKeyCompositionKind.Composed, Sensitive.Of(composed[index].ToString()));
    }
}
