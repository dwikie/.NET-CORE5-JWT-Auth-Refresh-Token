using System.ComponentModel.DataAnnotations;

namespace TodoApp.Models.DTOs.Requests
{
    public class UserLoginModel 
    {
        [Required]
        public string Username { get; set; }

        [Required]
        public string Password { get; set; }
    }
}