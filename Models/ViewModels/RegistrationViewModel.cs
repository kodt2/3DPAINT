using System;
using System.ComponentModel.DataAnnotations;

namespace LoginAndReg.Models.ViewModels
{
    public class RegistrationViewModel
    {
        [Required(ErrorMessage = "Full Name is required")]
        [StringLength(100, ErrorMessage = "Full Name cannot be longer than 100 characters")]
        public string FullName { get; set; }

        [Required(ErrorMessage = "Birth Date is required")]
        [DataType(DataType.Date)]
        public DateTime BirthDate { get; set; }

        [Required(ErrorMessage = "Login is required")]
        [StringLength(50, ErrorMessage = "Login cannot be longer than 50 characters")]
        public string Login { get; set; }

        [Required(ErrorMessage = "Password is required")]
        [DataType(DataType.Password)]
        [StringLength(100, MinimumLength = 6, ErrorMessage = "Password must be at least 6 characters")]
        public string Password { get; set; }
    }
}
