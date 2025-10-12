using Microsoft.EntityFrameworkCore;

namespace RadegastWeb.Data
{
    public class DbContextFactory<T> : IDbContextFactory<T> where T : DbContext
    {
        private readonly DbContextOptions<T> _options;

        public DbContextFactory(DbContextOptions<T> options)
        {
            _options = options;
        }

        public T CreateDbContext()
        {
            return (T)Activator.CreateInstance(typeof(T), _options)!;
        }
    }
}