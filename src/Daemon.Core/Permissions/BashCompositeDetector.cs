namespace Daemon.Core.Permissions;

public sealed class BashCompositeDetector : IBashCompositeDetector
{
    // Operators and patterns that make a command composite.
    // Single-quoted strings are opaque: their contents are never examined.
    // Double-quoted strings are NOT opaque: $(…) inside them is still composite.
    public bool IsComposite(string command)
    {
        if (string.IsNullOrEmpty(command))
        {
            return false;
        }

        // Check inline env assignments before the first non-assignment word.
        if (HasInlineEnvAssignment(command))
        {
            return true;
        }

        bool inSingleQuote = false;
        int i = 0;
        int length = command.Length;

        while (i < length)
        {
            char c = command[i];

            if (c == '\'' && !inSingleQuote)
            {
                // Enter single-quoted region — advance until closing quote.
                inSingleQuote = true;
                i++;
                while (i < length && command[i] != '\'')
                {
                    i++;
                }
                // Skip the closing quote, or end-of-string (ambiguous → composite).
                if (i >= length)
                {
                    return true; // unclosed single quote — ambiguous, fail safe
                }
                inSingleQuote = false;
                i++;
                continue;
            }

            // From here: outside single-quoted strings.

            switch (c)
            {
                case '|':
                    // Pipe (|) or pipe-stderr (|&) or logical-or (||)
                    return true;

                case ';':
                    return true;

                case '&':
                    // Backgrounding (&), and-and (&&), but NOT &> or >& (already caught by >)
                    // Any & that is not part of && was already covered by | above for ||.
                    // We check &> separately under >.
                    // Reaching here: & is backgrounding or &&, both are composite.
                    return true;

                case '\n':
                    return true;

                case '`':
                    // Backtick command substitution
                    return true;

                case '$':
                    if (i + 1 < length && command[i + 1] == '(')
                    {
                        return true; // $( command substitution
                    }
                    break;

                case '<':
                    // Redirect (<, <<, <<<) or process substitution <(
                    return true;

                case '>':
                    // Redirect (>, >>, >&) or process substitution >(
                    return true;

                case '(':
                    // Subshell
                    return true;

                case '{':
                    // Command group
                    return true;
            }

            // Check 2> redirect: digit followed by >
            if (char.IsAsciiDigit(c) && i + 1 < length && command[i + 1] == '>')
            {
                return true;
            }

            i++;
        }

        return false;
    }

    private static bool HasInlineEnvAssignment(string command)
    {
        // An inline env assignment is a sequence of IDENTIFIER=value words before the first
        // non-assignment word. IDENTIFIER is [A-Za-z_][A-Za-z0-9_]*.
        // We tokenise by whitespace (not shell-splitting) and check leading tokens.
        ReadOnlySpan<char> remaining = command.AsSpan().TrimStart();

        while (!remaining.IsEmpty)
        {
            int spaceIdx = remaining.IndexOfAny(' ', '\t');
            ReadOnlySpan<char> token = spaceIdx < 0 ? remaining : remaining[..spaceIdx];

            int eq = token.IndexOf('=');
            if (eq <= 0)
            {
                // No '=' in this token — it's the actual command, no env assignment found.
                return false;
            }

            ReadOnlySpan<char> name = token[..eq];
            if (!IsValidIdentifier(name))
            {
                return false;
            }

            // This token is an assignment; check if there is a following token.
            if (spaceIdx < 0)
            {
                // The whole command is just assignments with no command — not composite.
                return false;
            }

            remaining = remaining[(spaceIdx + 1)..].TrimStart();

            if (!remaining.IsEmpty)
            {
                // There is a subsequent word — this is an inline env assignment before a command.
                return true;
            }
        }

        return false;
    }

    private static bool IsValidIdentifier(ReadOnlySpan<char> name)
    {
        if (name.IsEmpty)
        {
            return false;
        }

        if (!char.IsLetter(name[0]) && name[0] != '_')
        {
            return false;
        }

        for (int i = 1; i < name.Length; i++)
        {
            if (!char.IsLetterOrDigit(name[i]) && name[i] != '_')
            {
                return false;
            }
        }

        return true;
    }
}
