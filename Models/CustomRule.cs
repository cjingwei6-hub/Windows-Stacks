using System;
using System.Collections.Generic;
using System.Linq;

namespace Stacks.Models;

/// <summary>
/// User-defined classification rule. Each rule gathers specified file extensions
/// into a single named stack (group). Rules are only consulted when the
/// GroupEngine is in GroupMode.Custom.
/// </summary>
public class CustomRule
{
    /// <summary>Stable id used as the group key (e.g. "custom:工作资料").</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];

    /// <summary>Display name shown on the stack.</summary>
    public string Name { get; set; } = "";

    /// <summary>Extensions this rule matches, lowercased with leading dot (".pdf", ".docx").</summary>
    public List<string> Extensions { get; set; } = new();

    /// <summary>Human-readable preview for UI: ".pdf, .docx, .xlsx".</summary>
    public string ExtensionsText =>
        Extensions.Count == 0 ? "(无扩展名)" : string.Join(", ", Extensions);
}
