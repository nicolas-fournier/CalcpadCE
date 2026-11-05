using System.Text;
using Calcpad.AI;

namespace Calcpad.Tests;

// Phase B4+ — persistent chat memory and multimodal attachments (image / PDF).
public class AiMemoryAttachmentTests
{
    [Fact]
    public void HistoryStore_RoundTrips_RoleAndText()
    {
        var path = Path.Combine(Path.GetTempPath(), "cpd_chat_" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var store = new JsonChatHistoryStore(path);
            var history = new List<ChatMessage>
            {
                ChatMessage.FromUser("compute 2+3"),
                ChatMessage.FromAssistant("The answer is 5."),
            };
            store.Save(history);

            var loaded = store.Load();
            Assert.Equal(2, loaded.Count);
            Assert.Equal(AiRole.User, loaded[0].Role);
            Assert.Equal("compute 2+3", loaded[0].Text);
            Assert.Equal(AiRole.Assistant, loaded[1].Role);
            Assert.Equal("The answer is 5.", loaded[1].Text);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void HistoryStore_Clear_RemovesFile()
    {
        var path = Path.Combine(Path.GetTempPath(), "cpd_chat_" + Guid.NewGuid().ToString("N") + ".json");
        var store = new JsonChatHistoryStore(path);
        store.Save([ChatMessage.FromUser("hi")]);
        Assert.NotEmpty(store.Load());

        store.Clear();
        Assert.Empty(store.Load());
    }

    [Fact]
    public void HistoryStore_DropsAttachments()
    {
        var path = Path.Combine(Path.GetTempPath(), "cpd_chat_" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var store = new JsonChatHistoryStore(path);
            var withImage = ChatMessage.FromUser("see this", [ChatAttachment.Image(Encoding.UTF8.GetBytes("fakepng"))]);
            store.Save([withImage]);

            var loaded = store.Load();
            Assert.Single(loaded);
            Assert.Equal("see this", loaded[0].Text);
            Assert.Empty(loaded[0].Attachments); // attachments are transient, not persisted
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void ImageAttachment_EncodesBase64AndMediaType()
    {
        var bytes = new byte[] { 1, 2, 3, 4 };
        var a = ChatAttachment.Image(bytes, "image/jpeg", "shot.jpg");
        Assert.Equal(AttachmentKind.Image, a.Kind);
        Assert.Equal("image/jpeg", a.MediaType);
        Assert.Equal(Convert.ToBase64String(bytes), a.Base64Data);
        Assert.Equal("shot.jpg", a.FileName);
    }

    [Fact]
    public void PdfAttachment_UsesApplicationPdf()
    {
        var a = ChatAttachment.Pdf(new byte[] { 9, 9 }, "spec.pdf");
        Assert.Equal(AttachmentKind.Pdf, a.Kind);
        Assert.Equal("application/pdf", a.MediaType);
    }

    [Fact]
    public void Message_CarriesAttachments()
    {
        var m = ChatMessage.FromUser("look", [ChatAttachment.Pdf([1, 2])]);
        Assert.Single(m.Attachments);
        Assert.Equal(AttachmentKind.Pdf, m.Attachments[0].Kind);
    }
}
