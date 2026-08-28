namespace XREngine.Core;

/// <summary>Parses stable type names from persisted CLR type identities.</summary>
public static class SerializedTypeIdentity
{
    /// <summary>
    /// Removes only the outer assembly qualification while preserving commas inside generic
    /// argument brackets.
    /// </summary>
    public static string GetUnqualifiedTypeName(string typeIdentity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(typeIdentity);

        int bracketDepth = 0;
        for (int index = 0; index < typeIdentity.Length; index++)
        {
            switch (typeIdentity[index])
            {
                case '[':
                    bracketDepth++;
                    break;
                case ']':
                    if (bracketDepth > 0)
                        bracketDepth--;
                    break;
                case ',' when bracketDepth == 0:
                    return typeIdentity[..index].Trim();
            }
        }

        return typeIdentity.Trim();
    }
}
