using System.Text;

namespace Tasktor.Core.Windows;

/// <summary>
/// A Windows command line split into arguments the way <c>CommandLineToArgvW</c> and the C runtime do: the program name
/// ends at whitespace or its closing quote (no escapes in it); after it, 2n backslashes before a quote are n backslashes
/// and the quote toggles quoting, 2n+1 are n and a literal quote, and backslashes elsewhere are literal. So a preset's
/// launch shown for review is exactly what <c>ProcessStartInfo.ArgumentList</c> will pass.
/// </summary>
public static class CommandLineArgs
{
    public static IReadOnlyList<string> Split(string commandLine)
    {
        ArgumentNullException.ThrowIfNull(commandLine);
        var args = new List<string>();
        var i = 0;
        var s = commandLine;
        while (i < s.Length && char.IsWhiteSpace(s[i]))
        {
            i++;
        }

        if (i >= s.Length)
        {
            return args;
        }

        // argv[0]: quoted or up to whitespace, taken literally.
        var program = new StringBuilder();
        if (s[i] == '"')
        {
            i++;
            while (i < s.Length && s[i] != '"')
            {
                program.Append(s[i++]);
            }

            i++;
        }
        else
        {
            while (i < s.Length && !char.IsWhiteSpace(s[i]))
            {
                program.Append(s[i++]);
            }
        }

        args.Add(program.ToString());
        while (true)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i]))
            {
                i++;
            }

            if (i >= s.Length)
            {
                return args;
            }

            var arg = new StringBuilder();
            var quoted = false;
            while (i < s.Length && (quoted || !char.IsWhiteSpace(s[i])))
            {
                if (s[i] == '\\')
                {
                    var n = 0;
                    while (i < s.Length && s[i] == '\\')
                    {
                        n++;
                        i++;
                    }

                    if (i < s.Length && s[i] == '"')
                    {
                        arg.Append('\\', n / 2);
                        if (n % 2 == 1)
                        {
                            arg.Append('"');
                            i++;
                        }
                    }
                    else
                    {
                        arg.Append('\\', n);
                    }
                }
                else if (s[i] == '"')
                {
                    if (quoted && i + 1 < s.Length && s[i + 1] == '"')
                    {
                        arg.Append('"'); // "" inside quotes is a literal quote (C runtime since 2008)
                        i += 2;
                        continue;
                    }

                    quoted = !quoted;
                    i++;
                }
                else
                {
                    arg.Append(s[i++]);
                }
            }

            args.Add(arg.ToString());
        }
    }
}
