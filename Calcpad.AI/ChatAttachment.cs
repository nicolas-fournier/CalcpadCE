namespace Calcpad.AI
{
    public enum AttachmentKind
    {
        Image,
        Pdf
    }

    /// <summary>
    /// A file attached to a user turn (a pasted image or an attached PDF), carried as base64 so it
    /// can be sent to Claude as a multimodal content block.
    /// </summary>
    public sealed record ChatAttachment(AttachmentKind Kind, string MediaType, string Base64Data, string FileName = "")
    {
        /// <summary>Creates an image attachment from raw bytes (e.g. a pasted bitmap encoded as PNG).</summary>
        public static ChatAttachment Image(byte[] bytes, string mediaType = "image/png", string fileName = "") =>
            new(AttachmentKind.Image, mediaType, Convert.ToBase64String(bytes), fileName);

        /// <summary>Creates a PDF attachment from raw bytes.</summary>
        public static ChatAttachment Pdf(byte[] bytes, string fileName = "") =>
            new(AttachmentKind.Pdf, "application/pdf", Convert.ToBase64String(bytes), fileName);

        public int ApproxBytes => (Base64Data?.Length ?? 0) * 3 / 4;
    }
}
