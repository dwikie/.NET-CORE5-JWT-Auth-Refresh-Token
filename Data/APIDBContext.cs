using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using TodoApp.Models;

namespace TodoApp.Data
{
    public class APIDBContext : IdentityDbContext
    {
        public virtual DbSet<ItemDataModel> Items { get; set; }
        public virtual DbSet<RefreshTokenModel> RefreshTokens { get; set; }
        public APIDBContext(DbContextOptions<APIDBContext> options) : base(options)
        {

        }
    }
}