using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using GitHubCostVisualizer.Web.Models;

namespace GitHubCostVisualizer.Web.Processor
{
    public interface IGithubUsageProcessor
    {
        UsageReportViewModel ProcessUsageReport(List<GithubUsageEntry> entries);
    }

    public class GithubUsageProcessor : IGithubUsageProcessor
    {
        public UsageReportViewModel ProcessUsageReport(List<GithubUsageEntry> entries)
        {
            var model = new UsageReportViewModel();
            model.StartDate = entries.Min(i => i.Date);
            model.EndDate = entries.Max(i => i.Date);
            model.TotalActionMinutesUsed =
                entries.Where(i => i.Product.Equals(Constants.GitHubProducts.Actions, StringComparison.InvariantCultureIgnoreCase)).Sum(i => (int)i.Quantity);
            model.TotalBillableActionMinutes = entries
                .Where(i => i.Product.Equals(Constants.GitHubProducts.Actions, StringComparison.InvariantCultureIgnoreCase))
                .Sum(i => (int)(i.Quantity * i.Multiplier));
            model.TotalActionMinutesCost = entries
                .Where(i => i.Product.Equals(Constants.GitHubProducts.Actions, StringComparison.InvariantCultureIgnoreCase))
                .Sum(i => i.Quantity.GetValueOrDefault() /** i.Multiplier.GetValueOrDefault()*/ * i.PricePer.GetValueOrDefault());
            model.ActionsSummary = (from x in entries.Where(i => i.Product.Equals(Constants.GitHubProducts.Actions, StringComparison.InvariantCultureIgnoreCase))
                                    group x by x.Sku
                    into grp
                                    select new KeyValuePair<string, int>(grp.Key, grp.Sum(i => (int)i.Quantity)))
                .ToList();
            model.ActionMinutesByRepository = (from x in entries.Where(i => i.Product.Equals(Constants.GitHubProducts.Actions, StringComparison.InvariantCultureIgnoreCase))
                                               group x by x.Repository
            into grp
                                               select new KeyValuePair<string, int>(grp.Key, grp.Sum(i => (int)i.Quantity))).ToList();

            model.DailyStorageSummary = (from x in entries.Where(i => i.Product.Equals(Constants.GitHubProducts.SharedStorage, StringComparison.InvariantCultureIgnoreCase))
                                         group x by x.Date
                    into grp
                                         select new KeyValuePair<DateTime, decimal>(grp.Key, grp.Sum(i => i.Quantity.GetValueOrDefault())))
                .ToList();

            model.DailyStorageByRepo = GenerateStorageByDays(entries);
            model.MonthlyActionByRepo = GenerateActionByMonth(entries);

            if (model.DailyStorageSummary.Count > 0)
            {
                model.AverageDailyStorage =
                    model.DailyStorageSummary.Sum(i => i.Value) / model.DailyStorageSummary.Count;

                var totalDays = model.DailyStorageSummary.Count();
                model.AverageDailyStorageByRepo = (from x in entries.Where(i => i.Product.Equals(Constants.GitHubProducts.SharedStorage, StringComparison.InvariantCultureIgnoreCase))
                                                   group x by x.Repository
                    into grp
                                                   select new KeyValuePair<string, decimal>(grp.Key, grp.Sum(i => i.Quantity.GetValueOrDefault()) / grp.Count())).ToList();
            }
            else
            {
                //Default values
                model.AverageDailyStorage = 0m;
                model.AverageDailyStorageByRepo = new List<KeyValuePair<string, decimal>>();
            }

            return model;
        }

        private DailyStorageData GenerateStorageByDays(List<GithubUsageEntry> entries)
        {
            var storage = entries.Where(e => e.Product.Equals(Constants.GitHubProducts.SharedStorage, StringComparison.InvariantCultureIgnoreCase)).ToList();
            if(!storage.Any())
                return new DailyStorageData();
            
            var startDate = storage.Min(r => r.Date);
            var endDate = storage.Max(r => r.Date);

            var repos = storage.Select(r => r.Repository).Distinct().OrderBy(r => r);
            var dayList = Enumerable.Range(0, 1 + endDate.Subtract(startDate).Days).Select(o => startDate.AddDays(o)).ToList();

            var q = from r in repos
                    from d in dayList
                    join s in storage on new { r, d } equals new { r = s.Repository, d = s.Date } into byDay
                    from bd in byDay.DefaultIfEmpty()
                    select new { Repo = r, Date = d, Quantity = bd?.Quantity ?? 0 };

            var results = q.GroupBy(k => k.Repo)
                .OrderBy(g => g.Key)
                .Select(g => new DailyStorageDataSet { Label = g.Key, Data = g.OrderBy(r => r.Date).Select(r => r.Quantity).ToList() });

            return new DailyStorageData
            {
                Labels = dayList.Select(d=>d.ToShortDateString()).ToList(),
                DataSets = results.ToList()
            };
        }

        private DailyStorageData GenerateActionByMonth(List<GithubUsageEntry> entries)
        {
            var storage = entries.Where(e => e.Product.Equals(Constants.GitHubProducts.Actions, StringComparison.InvariantCultureIgnoreCase)).ToList();
            if (!storage.Any())
                return new DailyStorageData();

            var startDate = storage.Min(r => r.Date);
            var endDate = storage.Max(r => r.Date);

            var repos = storage.Select(r => r.Repository).Distinct().OrderBy(r => r);

            var monthList = new List<DateTime>();
            var monthIndex = new Dictionary<long, int>();
            var curDate = startDate;
            var ii = 0;
            while (true)
            {
                var month = new DateTime(curDate.Year, curDate.Month, 1);
                monthList.Add(month);
                monthIndex.Add(month.Ticks, ii++);
                if (curDate > endDate)
                    break;
                curDate = curDate.AddMonths(1);
            }
            var actionsDataSet = new Dictionary<string,DailyStorageDataSet>();

            foreach(var entry in storage)
            {
                var repo = entry.Repository;
                var month = new DateTime(entry.Date.Year, entry.Date.Month, 1);
                var totalActionMinutesCost = entry.Quantity.GetValueOrDefault() * entry.PricePer.GetValueOrDefault();

                DailyStorageDataSet ds;
                if (!actionsDataSet.TryGetValue(repo, out ds))
                {
                    ds = new DailyStorageDataSet { Label = repo, Data = new List<decimal>(monthList.Count) };
                    actionsDataSet.Add(repo, ds);
                    for (int j = 0; j < monthList.Count; j++)
                        ds.Data.Add(0);
                }


                int monthId = monthIndex[month.Ticks];
                ds.Data[monthId] += totalActionMinutesCost;
            }


            /*var q = from r in repos
                    from d in monthList
                    join s in storage on new { r, d } equals new { r = s.Repository, d = new DateTime(s.Date.Year, s.Date.Month,1) } into byMonth
                    from bd in byMonth.DefaultIfEmpty()
                    select new { Repo = r, Date = d, Quantity = bd?.Quantity ?? 0 };

            var results = q.GroupBy(k => k.Repo)
                .OrderBy(g => g.Key)
                .Select(g => new DailyStorageDataSet { Label = g.Key, Data = g.OrderBy(r => r.Date).Select(r => r.Quantity).ToList() });
            */

            return new DailyStorageData
            {
                Labels = monthList.Select(d => d.ToShortDateString()).ToList(),
                DataSets = actionsDataSet.Values.ToList(),
            };
        }
    }
}
