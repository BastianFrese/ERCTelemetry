namespace ERCTelemetry.Core.Llm;

/// <summary>App-wide LLM constants.</summary>
public static class LlmConstants
{
    /// <summary>The ONLY model the LLM layer ever uses. Locked on purpose: the app should
    /// stay cheap and an end user must not be able to select an arbitrarily expensive
    /// model. This is the compact/cost-efficient DeepSeek model as published on the
    /// Ollama cloud.</summary>
    public const string FixedModel = "deepseek-v4-flash:cloud";
}
