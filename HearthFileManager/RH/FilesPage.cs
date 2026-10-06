using System;
using System.Text;
using HearthFileManager.engine;

namespace HearthFileManager.RH
{
    public class FilesPage
    {
        public const int ChunkBytes = 5 * 1024 * 1024;

        public static void HandleRequest()
        {
            if (!Guard.PageLoggedIn()) return;
            obConfig cfg = AppConfig.Get();

            var pt = new PageTemplate { Title = "My Files", BodyClass = "page-files", ActiveMenu = "files" };
            pt.ExtraHeaderText = StaticAsset.Css("/css/files.css") + "\n" + StaticAsset.Css("/css/components/hearth-editor.css");
            pt.ExtraFooterText = PageTemplate.DataIsland("FILES_CONFIG", new
            {
                MaxUploadBytes = (long)cfg.MaxUploadMb * 1024 * 1024,
                ChunkBytes,
                CanEdit = AppSession.LoginUser.Has(Perm.Files),
                SitePreviewUrl = cfg.SitePreviewUrl
            }) + "\n" + StaticAsset.Script("/js/components/hearth-editor.js") + "\n" + StaticAsset.Script("/js/files.js");

            var sb = new StringBuilder();
            sb.Append(pt.GenerateHtmlHeader());
            sb.Append(@"
<div class='fm' id='fm'>
    <header class='fm-header'>
        <nav class='breadcrumb' id='fm-breadcrumb'></nav>
        <div class='fm-search'>
            <i class='fa-solid fa-magnifying-glass'></i>
            <input type='search' id='fm-filter' placeholder='Filter this folder…' />
        </div>
    </header>

    <div class='fm-toolbar' id='fm-toolbar'>
        <div class='tb-group' data-when='normal'>
            <button type='button' class='btn btn-primary' data-act='upload'><i class='fa-solid fa-cloud-arrow-up'></i><span>Upload</span></button>
            <button type='button' class='btn' data-act='new-folder'><i class='fa-solid fa-folder-plus'></i><span>New folder</span></button>
            <button type='button' class='btn' data-act='new-file'><i class='fa-solid fa-file-circle-plus'></i><span>New file</span></button>
            <span class='ro-badge'><i class='fa-solid fa-eye'></i> View only – you can open and download files</span>
            <span class='dnd-hint'><i class='fa-solid fa-hand-pointer'></i> Tip: drag &amp; drop files or folders from your computer here to upload</span>
        </div>
        <div class='tb-group' data-when='recycle'>
            <button type='button' class='btn btn-danger' data-act='empty-recycle'><i class='fa-solid fa-dumpster'></i><span>Empty recycle bin</span></button>
        </div>
        <div class='tb-group tb-selection' id='tb-selection'>
            <span class='sel-count' id='sel-count'></span>
            <button type='button' class='btn' data-act='restore' data-when='recycle'><i class='fa-solid fa-trash-arrow-up'></i><span>Restore</span></button>
            <button type='button' class='btn' data-act='edit' data-when='normal' data-single='text'><i class='fa-solid fa-pen-to-square'></i><span>Edit</span></button>
            <button type='button' class='btn' data-act='download'><i class='fa-solid fa-download'></i><span>Download</span></button>
            <button type='button' class='btn' data-act='rename' data-when='normal' data-single='any'><i class='fa-solid fa-i-cursor'></i><span>Rename</span></button>
            <button type='button' class='btn' data-act='move' data-when='normal'><i class='fa-solid fa-arrows-up-down-left-right'></i><span>Move</span></button>
            <button type='button' class='btn' data-act='copy' data-when='normal'><i class='fa-solid fa-copy'></i><span>Copy</span></button>
            <button type='button' class='btn' data-act='zip' data-when='normal'><i class='fa-solid fa-file-zipper'></i><span>Zip</span></button>
            <button type='button' class='btn' data-act='unzip' data-when='normal' data-single='zip'><i class='fa-solid fa-box-open'></i><span>Unzip</span></button>
            <button type='button' class='btn btn-danger-ghost' data-act='delete'><i class='fa-solid fa-trash-can'></i><span>Delete</span></button>
        </div>
        <div class='tb-spacer'></div>
        <div class='view-toggle' role='group' aria-label='View'>
            <button type='button' class='btn-icon' data-view='list' title='List view'><i class='fa-solid fa-list'></i></button>
            <button type='button' class='btn-icon' data-view='grid' title='Grid view'><i class='fa-solid fa-table-cells-large'></i></button>
        </div>
    </div>

    <section class='fm-body' id='fm-body'>
        <div class='fm-content' id='fm-content'></div>
        <div class='drop-overlay' id='drop-overlay'>
            <div><i class='fa-solid fa-cloud-arrow-up'></i><p>Drop files or folders to upload</p><small id='drop-target'></small></div>
        </div>
    </section>
</div>

<input type='file' id='fm-file-input' multiple hidden />

<aside class='upload-panel' id='upload-panel'>
    <header><strong id='upload-title'>Uploads</strong><button type='button' class='btn-icon' id='upload-close' title='Close'><i class='fa-solid fa-xmark'></i></button></header>
    <ul id='upload-list'></ul>
</aside>

<div class='ctx-menu' id='ctx-menu'></div>

<div class='lightbox' id='lightbox'>
    <button type='button' class='btn-icon lb-close' data-lb='close'><i class='fa-solid fa-xmark'></i></button>
    <button type='button' class='btn-icon lb-prev' data-lb='prev'><i class='fa-solid fa-chevron-left'></i></button>
    <figure><img id='lightbox-img' alt='' /><figcaption id='lightbox-cap'></figcaption></figure>
    <button type='button' class='btn-icon lb-next' data-lb='next'><i class='fa-solid fa-chevron-right'></i></button>
</div>");
            sb.Append(pt.GenerateHtmlFooter());
            PageTemplate.WriteHtml(sb.ToString());
        }
    }
}
