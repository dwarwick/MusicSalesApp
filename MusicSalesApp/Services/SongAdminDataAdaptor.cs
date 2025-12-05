using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MusicSalesApp.Models;
using Syncfusion.Blazor;
using Syncfusion.Blazor.Data;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace MusicSalesApp.Services
{
    /// <summary>
    /// Custom data adaptor for Syncfusion Grid to support server-side operations
    /// </summary>
    public class SongAdminDataAdaptor : DataAdaptor
    {
        [Inject]
        public ISongAdminService SongAdminService { get; set; }
        
        // Static properties to hold filter values (set from component)
        public static string FilterAlbumName { get; set; } = string.Empty;
        public static string FilterSongTitle { get; set; } = string.Empty;
        public static string FilterGenre { get; set; } = string.Empty;
        public static string FilterType { get; set; } = string.Empty;

        public override async Task<object> ReadAsync(DataManagerRequest dm, string key = null)
        {
            var parameters = new SongQueryParameters
            {
                Skip = dm.Skip,
                Take = dm.Take,
                FilterAlbumName = FilterAlbumName,
                FilterSongTitle = FilterSongTitle,
                FilterGenre = FilterGenre,
                FilterType = FilterType
            };

            // Handle sorting from Grid
            if (dm.Sorted != null && dm.Sorted.Count > 0)
            {
                var sort = dm.Sorted[0];
                parameters.SortColumn = sort.Name;
                parameters.SortAscending = sort.Direction == "ascending";
            }

            var result = await SongAdminService.GetSongsAsync(parameters);

            return dm.RequiresCounts 
                ? new DataResult { Result = result.Items, Count = result.TotalCount }
                : (object)result.Items;
        }
    }
}
