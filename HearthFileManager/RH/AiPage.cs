using System;
using System.Text;
using HearthFileManager.engine;

namespace HearthFileManager.RH
{
    public class AiPage
    {
        public static void HandleRequest()
        {
            if (!Guard.PageRequire(Perm.Ai, "ai")) return;
            obConfig cfg = AppConfig.Get();

            var pt = new PageTemplate { Title = "AI Website Builder", BodyClass = "page-ai", ActiveMenu = "ai" };
            pt.ExtraHeaderText = StaticAsset.Css("/css/ai.css") + "\n" + StaticAsset.Css("/css/components/hearth-editor.css");
            pt.ExtraFooterText = PageTemplate.DataIsland("AI_CONFIG", new
            {
                HasKey = !string.IsNullOrWhiteSpace(cfg.GeminiApiKey),
                Model = cfg.GeminiModel,
                SitePreviewUrl = cfg.SitePreviewUrl,
                Username = AppSession.LoginUser.Username
            }) + "\n" + StaticAsset.Script("/js/components/hearth-store.js")
               + "\n" + StaticAsset.Script("/js/components/hearth-editor.js")
               + "\n" + StaticAsset.Script("/js/ai.js");

            var sb = new StringBuilder();
            sb.Append(pt.GenerateHtmlHeader());
            sb.Append(@"
<div class='ai-layout'>
    <aside class='ai-chats' id='ai-chats'>
        <button type='button' class='btn btn-primary btn-block' id='ai-new-chat'><i class='fa-solid fa-plus'></i> New conversation</button>
        <ul class='chat-list' id='chat-list'></ul>
        <p class='hint'><i class='fa-solid fa-circle-info'></i> Conversations are saved in this browser only.</p>
    </aside>

    <section class='ai-main'>
        <header class='ai-header'>
            <button type='button' class='btn-icon' id='ai-chats-toggle' title='Conversations'><i class='fa-solid fa-comments'></i></button>
            <h1 id='ai-title'>AI Website Builder</h1>
            <div class='ai-header-actions'>
                <a class='btn' id='ai-preview' target='_blank' rel='noopener' hidden><i class='fa-solid fa-up-right-from-square'></i><span>View website</span></a>
                <button type='button' class='btn-icon' id='ai-export' title='Export conversation'><i class='fa-solid fa-file-export'></i></button>
            </div>
        </header>

        <div class='ai-messages' id='ai-messages'></div>

        <form class='ai-composer' id='ai-composer'>
            <div class='attach-strip' id='attach-strip'></div>
            <div class='composer-row'>
                <label class='btn-icon' title='Attach a picture (or paste one)'>
                    <i class='fa-solid fa-image'></i>
                    <input type='file' id='ai-attach' accept='image/*' multiple hidden />
                </label>
                <textarea id='ai-input' rows='1' placeholder='Describe what you want to build or change…'></textarea>
                <button type='submit' class='btn btn-primary' id='ai-send'><i class='fa-solid fa-paper-plane'></i><span>Send</span></button>
                <button type='button' class='btn btn-danger' id='ai-stop' hidden><i class='fa-solid fa-stop'></i><span>Stop</span></button>
            </div>
        </form>
    </section>
</div>");
            sb.Append(pt.GenerateHtmlFooter());
            PageTemplate.WriteHtml(sb.ToString());
        }
    }
}
