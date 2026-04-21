namespace LoginAndReg.Models.ViewModels
{
    public class FileUploadViewModel
    {
        public int UserId { get; set; }
        public IFormFile File { get; set; } = null!;
    }
}