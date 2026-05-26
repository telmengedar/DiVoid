using System;
using System.Text;
using Backend.Services.Embeddings;

namespace Backend.Models.Nodes;

/// <summary>
/// pure helper that encodes a node's raw content bytes into the inline JSON representation
/// used by the <c>content</c> field on <see cref="NodeDetails"/> listing rows.
///
/// text content (per <see cref="TextContentTypePredicate.IsText"/>) is decoded as UTF-8 and
/// returned as a JSON string.  binary content (including unknown or null <c>contentType</c>)
/// is base64-encoded.  a null or empty byte array returns null.
/// </summary>
internal static class InlineContentEncoder
{
    /// <summary>
    /// encodes raw content bytes for inline listing inclusion.
    /// </summary>
    /// <param name="content">raw content bytes from <c>Node.Content</c>; null or empty yields null.</param>
    /// <param name="contentType">MIME type string from <c>Node.ContentType</c>; null is treated as binary.</param>
    /// <returns>UTF-8 string for text content; base64 string for binary content; null when content is absent.</returns>
    internal static string Encode(byte[] content, string contentType)
    {
        if (content == null || content.Length == 0)
            return null;
        return TextContentTypePredicate.IsText(contentType)
            ? Encoding.UTF8.GetString(content)
            : Convert.ToBase64String(content);
    }
}
