using System;

namespace RadegastWeb.Services
{
    /// <summary>
    /// Service for handling Second Life Time (SLT) conversions and formatting
    /// SLT is equivalent to Pacific Standard Time (PST) / Pacific Daylight Time (PDT)
    /// </summary>
    public interface ISLTimeService
    {
        /// <summary>
        /// Convert UTC DateTime to SLT (Pacific Time)
        /// </summary>
        DateTime ConvertToSLT(DateTime utcTime);
        
        /// <summary>
        /// Get current SLT time
        /// </summary>
        DateTime GetCurrentSLT();
        
        /// <summary>
        /// Format a DateTime as SLT string
        /// </summary>
        string FormatSLT(DateTime utcTime, string format = "HH:mm:ss");
        
        /// <summary>
        /// Format a DateTime as SLT string with date
        /// </summary>
        string FormatSLTWithDate(DateTime utcTime, string format = "MMM dd, HH:mm:ss");
        
        /// <summary>
        /// Get SLT timezone info
        /// </summary>
        TimeZoneInfo GetSLTTimeZone();
    }
    
    public class SLTimeService : ISLTimeService
    {
        private readonly TimeZoneInfo _sltTimeZone;
        
        public SLTimeService()
        {
            // Pacific Standard Time zone - this handles PST/PDT automatically
            _sltTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time");
        }
        
        public DateTime ConvertToSLT(DateTime utcTime)
        {
            if (utcTime.Kind != DateTimeKind.Utc)
            {
                // Assume it's UTC if not specified
                utcTime = DateTime.SpecifyKind(utcTime, DateTimeKind.Utc);
            }
            
            return TimeZoneInfo.ConvertTimeFromUtc(utcTime, _sltTimeZone);
        }
        
        public DateTime GetCurrentSLT()
        {
            return ConvertToSLT(DateTime.UtcNow);
        }
        
        public string FormatSLT(DateTime utcTime, string format = "HH:mm:ss")
        {
            var sltTime = ConvertToSLT(utcTime);
            return sltTime.ToString(format);
        }
        
        public string FormatSLTWithDate(DateTime dateTime, string format = "MMM dd, HH:mm:ss")
        {
            // Check if the input is already a date-only value (likely already in SLT)
            if (dateTime.TimeOfDay == TimeSpan.Zero)
            {
                // This is likely already an SLT date, don't convert
                return dateTime.ToString(format);
            }
            else
            {
                // This has a time component, treat as UTC and convert
                var sltTime = ConvertToSLT(dateTime);
                return sltTime.ToString(format);
            }
        }
        
        public TimeZoneInfo GetSLTTimeZone()
        {
            return _sltTimeZone;
        }
    }
}