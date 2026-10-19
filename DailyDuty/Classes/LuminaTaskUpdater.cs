using System;
using System.Collections.Generic;
using System.Linq;
using DailyDuty.Models;
using DailyDuty.Modules.BaseModules;
using Lumina.Excel;

namespace DailyDuty.Classes;

public class LuminaTaskUpdater<T> where T : struct, IExcelRow<T> {
    private readonly Module module;
    private readonly List<T> luminaRows;

    /// <summary>依列內容篩選：整張表只掃這一次。</summary>
    public LuminaTaskUpdater(Module module, Func<T, bool> filter) {
        this.module = module;
        luminaRows = Service.DataManager.GetExcelSheet<T>().Where(filter).ToList();
    }

    /// <summary>已知 row id：以 row id 直接取列、不掃表；表裡沒有的 id 略過，與依內容篩選的結果相同。</summary>
    public LuminaTaskUpdater(Module module, params uint[] rowIds) {
        this.module = module;

        var sheet = Service.DataManager.GetExcelSheet<T>();
        luminaRows = new List<T>(rowIds.Length);
        foreach (var rowId in rowIds) {
            if (sheet.TryGetRow(rowId, out var row)) {
                luminaRows.Add(row);
            }
        }
    }

    public void UpdateConfig(LuminaTaskConfigList<T> configValues) {
        if (configValues.Count != luminaRows.Count) {
            foreach (var luminaEntry in luminaRows) {
                if (!configValues.Any(task => task.RowId == luminaEntry.RowId)) {
                    configValues.Add(new LuminaTaskConfig<T> {
                        RowId = luminaEntry.RowId,
                        Enabled = false,
                        TargetCount = 0,
                    });
                }
            }
            
            module.SaveConfig();
        }

        configValues.Sort();
    }
    
    public void UpdateData(LuminaTaskDataList<T> dataList) {
        if (dataList.Count != luminaRows.Count) {
            foreach (var luminaEntry in luminaRows) {
                if (!dataList.Any(task => task.RowId == luminaEntry.RowId)) {
                    dataList.Add(new LuminaTaskData<T> {
                        RowId = luminaEntry.RowId,
                        Complete = false,
                        CurrentCount = 0,
                    });
                }
            }
            
            module.SaveData();
        }

        dataList.Sort();
    }
}