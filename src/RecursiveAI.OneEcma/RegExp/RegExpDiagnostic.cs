namespace RecursiveAI.OneEcma.RegExp;

/// <summary>
/// A problem found in a regular expression pattern or its flags. A pattern error is an early error: the span is
/// an exact range of the pattern text (UTF-16 code units, zero-based).
/// </summary>
/// <param name="Start">Index of the first code unit of the offending text in the pattern; for flag errors, in the flags string.</param>
/// <param name="Length">Length of the offending text in code units; at least 1 unless the problem is the end of the pattern.</param>
/// <param name="Reason">The part after the last colon of V8's message, for example <c>Unterminated group</c>.</param>
/// <param name="Message">The complete V8 message, for example <c>Invalid regular expression: /(/: Unterminated group</c>.</param>
/// <param name="InFlags">True when the problem is in the flags rather than the pattern.</param>
public readonly record struct RegExpDiagnostic(int Start, int Length, string Reason, string Message, bool InFlags = false);
