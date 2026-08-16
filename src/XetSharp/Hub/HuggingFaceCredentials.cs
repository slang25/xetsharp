namespace XetSharp.Hub;

/// <summary>
/// Finds a Hugging Face access token the same way the official tooling does, so a machine that has
/// already run <c>hf auth login</c> needs no further configuration. Public repositories work without
/// a token at all — the Hub issues anonymous Xet read tokens for them.
/// </summary>
public static class HuggingFaceCredentials
{
    /// <summary>
    /// The first token found among <c>HF_TOKEN</c>, <c>HUGGING_FACE_HUB_TOKEN</c>, the file named by
    /// <c>HF_TOKEN_PATH</c>, and <c>$HF_HOME/token</c> (defaulting to
    /// <c>~/.cache/huggingface/token</c>); null if there is none.
    /// </summary>
    public static string? ResolveToken()
    {
        foreach (var variable in (string[])["HF_TOKEN", "HUGGING_FACE_HUB_TOKEN"])
        {
            if (Environment.GetEnvironmentVariable(variable) is { Length: > 0 } token && !string.IsNullOrWhiteSpace(token))
            {
                return token.Trim();
            }
        }

        return ReadTokenFile(TokenFilePath());
    }

    private static string? TokenFilePath()
    {
        if (Environment.GetEnvironmentVariable("HF_TOKEN_PATH") is { Length: > 0 } explicitPath)
        {
            return explicitPath;
        }

        var home = Environment.GetEnvironmentVariable("HF_HOME");
        if (string.IsNullOrWhiteSpace(home))
        {
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrEmpty(userProfile))
            {
                return null;
            }

            home = Path.Combine(userProfile, ".cache", "huggingface");
        }

        return Path.Combine(home, "token");
    }

    private static string? ReadTokenFile(string? path)
    {
        if (path is null || !File.Exists(path))
        {
            return null;
        }

        try
        {
            var token = File.ReadAllText(path).Trim();
            return token.Length == 0 ? null : token;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
