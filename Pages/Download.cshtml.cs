using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MiniDl.Pages;

public class DownloadModel : PageModel
{
    public string DownloadUrl { get; set; } = "";

    public void OnGet(string? url)
    {
        DownloadUrl = url ?? "";
    }
}
