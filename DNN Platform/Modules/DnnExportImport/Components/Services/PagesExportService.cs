﻿#region Copyright
//
// DotNetNuke® - http://www.dnnsoftware.com
// Copyright (c) 2002-2017
// by DotNetNuke Corporation
//
// Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated
// documentation files (the "Software"), to deal in the Software without restriction, including without limitation
// the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and
// to permit persons to whom the Software is furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all copies or substantial portions
// of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED
// TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL
// THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF
// CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
// DEALINGS IN THE SOFTWARE.
#endregion

using System;
using System.Collections.Generic;
using System.Linq;
using Dnn.ExportImport.Components.Common;
using Dnn.ExportImport.Components.Controllers;
using Dnn.ExportImport.Components.Dto;
using Dnn.ExportImport.Components.Engines;
using Dnn.ExportImport.Components.Entities;
using Dnn.ExportImport.Dto.Pages;
using DotNetNuke.Application;
using DotNetNuke.Common.Utilities;
using DotNetNuke.Data;
using DotNetNuke.Entities.Modules.Definitions;
using DotNetNuke.Entities.Modules;
using DotNetNuke.Entities.Portals;
using DotNetNuke.Entities.Tabs;
using DotNetNuke.Framework;
using DotNetNuke.Instrumentation;
using DotNetNuke.Security.Permissions;
using Newtonsoft.Json;
// ReSharper disable SuggestBaseTypeForParameter

namespace Dnn.ExportImport.Components.Services
{
    /// <summary>
    /// Service to export/import pages/tabs.
    /// </summary>
    public class PagesExportService : BasePortableService
    {
        public override string Category => Constants.Category_Pages;

        public override string ParentCategory => null;

        public override uint Priority => 20;

        public virtual bool IncludeSystem { get; set; } = false;

        protected ImportDto ImportDto => _importDto;

        private ProgressTotals _totals;
        private Providers.DataProvider _dataProvider;
        private ITabController _tabController;
        private IModuleController _moduleController;
        private ExportImportJob _exportImportJob;
        private ImportDto _importDto;
        private ExportDto _exportDto;

        private static readonly ILog Logger = LoggerSource.Instance.GetLogger(typeof(ExportImportEngine));

        public override void ExportData(ExportImportJob exportJob, ExportDto exportDto)
        {
            if (CheckPoint.Stage > 0) return;
            if (CheckCancelled(exportJob)) return;

            var checkedPages = exportDto.Pages.Where(p => p.CheckedState == TriCheckedState.Checked || p.CheckedState == TriCheckedState.Partial);
            if (checkedPages.Any())
            {
                _exportImportJob = exportJob;
                _exportDto = exportDto;
                _tabController = TabController.Instance;
                _moduleController = ModuleController.Instance;
                ProcessExportPages();
            }

            CheckPoint.Progress = 100;
            CheckPoint.Stage++;
            CheckPoint.StageData = null;
            CheckPointStageCallback(this);
        }

        public override void ImportData(ExportImportJob importJob, ImportDto importDto)
        {
            if (CheckPoint.Stage > 0) return;
            if (CheckCancelled(importJob)) return;

            _exportImportJob = importJob;
            _importDto = importDto;
            _tabController = TabController.Instance;
            _moduleController = ModuleController.Instance;

            ProcessImportPages();

            CheckPoint.Progress = 100;
            CheckPoint.Stage++;
            CheckPoint.StageData = null;
            CheckPointStageCallback(this);
        }

        public override int GetImportTotal()
        {
            return Repository.GetCount<ExportTab>();
        }

        #region import methods

        private void ProcessImportPages()
        {
            _dataProvider = Providers.DataProvider.Instance();
            _totals = string.IsNullOrEmpty(CheckPoint.StageData)
                ? new ProgressTotals()
                : JsonConvert.DeserializeObject<ProgressTotals>(CheckPoint.StageData);

            var portalId = _exportImportJob.PortalId;

            var localTabs = _tabController.GetTabsByPortal(portalId).Values;

            var exportedTabs = Repository.GetAllItems<ExportTab>().ToList(); // ordered by TabID
            //Update the total items count in the check points. This should be updated only once.
            CheckPoint.TotalItems = CheckPoint.TotalItems <= 0 ? exportedTabs.Count : CheckPoint.TotalItems;
            if (CheckPointStageCallback(this)) return;
            var progressStep = 100.0 / exportedTabs.OrderByDescending(x => x.Id).Count(x => x.Id < _totals.LastProcessedId);

            foreach (var otherTab in exportedTabs)
            {
                if (CheckCancelled(_exportImportJob)) break;
                if (_totals.LastProcessedId > otherTab.Id) continue; // this is the exported DB row ID; not the TabID

                ProcessImportPage(otherTab, exportedTabs, localTabs.ToList());

                CheckPoint.ProcessedItems++;
                CheckPoint.Progress += progressStep;
                if (CheckPointStageCallback(this)) break;

                _totals.LastProcessedId = otherTab.Id;
                CheckPoint.StageData = JsonConvert.SerializeObject(_totals);
            }
            ReportImportTotals();
        }

        protected virtual void ProcessImportPage(ExportTab otherTab, IList<ExportTab> exportedTabs, IList<TabInfo> localTabs)
        {
            var portalId = _exportImportJob.PortalId;
            var createdBy = Util.GetUserIdByName(_exportImportJob, otherTab.CreatedByUserID, otherTab.CreatedByUserName);
            var modifiedBy = Util.GetUserIdByName(_exportImportJob, otherTab.LastModifiedByUserID, otherTab.LastModifiedByUserName);
            var localTab = localTabs.FirstOrDefault(t => t.TabName == otherTab.TabName && t.TabPath == otherTab.TabPath);

            if (localTab != null)
            {
                switch (_importDto.CollisionResolution)
                {
                    case CollisionResolution.Ignore:
                        Result.AddLogEntry("Ignored Tab", otherTab.TabName + " (" + otherTab.TabPath + ")");
                        break;
                    case CollisionResolution.Overwrite:
                        SetTabData(localTab, otherTab);
                        var parentId = GetParentLocalTabId(otherTab.TabId, exportedTabs, localTabs);
                        if (localTab.ParentId == -1 && otherTab.ParentId > 0)
                        {
                            Result.AddLogEntry("WARN: Imported existing TAB parent NOT found", otherTab.TabName + " (" + otherTab.TabPath + ")");
                        }
                        else
                        {
                            localTab.ParentId = parentId;
                        }

                        _tabController.UpdateTab(localTab);
                        UpdateTabChangers(localTab.TabID, createdBy, modifiedBy);
                        AddTabRelatedItems(localTab, otherTab, false);
                        Result.AddLogEntry("Updated Tab", otherTab.TabName + " (" + otherTab.TabPath + ")");
                        _totals.TotalTabs++;
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(_importDto.CollisionResolution.ToString());
                }
            }
            else
            {
                localTab = new TabInfo { PortalID = portalId };
                SetTabData(localTab, otherTab);
                localTab.ParentId = GetParentLocalTabId(otherTab.TabId, exportedTabs, localTabs);
                if (localTab.ParentId == -1 && otherTab.ParentId > 0)
                {
                    Result.AddLogEntry("WARN: Imported new TAB parent NOT found", otherTab.TabName + " (" + otherTab.TabPath + ")");
                }

                otherTab.LocalId = _tabController.AddTab(localTab);
                UpdateTabChangers(localTab.TabID, createdBy, modifiedBy);
                AddTabRelatedItems(localTab, otherTab, true);
                Result.AddLogEntry("Added Tab", otherTab.TabName + " (" + otherTab.TabPath + ")");
                _totals.TotalTabs++;
            }
        }

        private void AddTabRelatedItems(TabInfo localTab, ExportTab otherTab, bool isNew)
        {
            _totals.TotalSettings += ImportTabSettings(localTab, otherTab, isNew);
            _totals.TotalPermissions += ImportTabPermissions(localTab, otherTab, isNew);
            _totals.TotalUrls += ImportTabUrls(localTab, otherTab, isNew);
            _totals.TotalAliasSkins += ImportTabAliasSkins(localTab, otherTab, isNew);
            _totals.TotalModules += ImportTabModulesAndRelatedItems(localTab, otherTab, isNew);
            _totals.TotalTabModules += ImportTabModules(localTab, otherTab, isNew);
        }

        private int ImportTabSettings(TabInfo localTab, ExportTab otherTab, bool isNew)
        {
            var tabSettings = Repository.GetRelatedItems<ExportTabSetting>(otherTab.ReferenceId ?? -1).ToList();
            foreach (var other in tabSettings)
            {
                var localValue = isNew ? string.Empty : localTab.TabSettings[other.SettingName].ToString();
                if (string.IsNullOrEmpty(localValue))
                {
                    _tabController.UpdateTabSetting(localTab.TabID, other.SettingName, other.SettingValue);
                    var createdBy = Util.GetUserIdByName(_exportImportJob, other.CreatedByUserID, other.CreatedByUserName);
                    var modifiedBy = Util.GetUserIdByName(_exportImportJob, other.LastModifiedByUserID, other.LastModifiedByUserName);
                    _dataProvider.UpdateSettingRecordChangers("TabSettings", "TabID",
                        localTab.TabID, other.SettingName, createdBy, modifiedBy);
                    Result.AddLogEntry("Added tab setting", other.SettingName);
                }
                else
                {
                    switch (_importDto.CollisionResolution)
                    {
                        case CollisionResolution.Overwrite:
                            if (localValue != other.SettingValue)
                            {
                                // the next will clear the cache
                                _tabController.UpdateTabSetting(localTab.TabID, other.SettingName, other.SettingValue);
                                var createdBy = Util.GetUserIdByName(_exportImportJob, other.CreatedByUserID, other.CreatedByUserName);
                                var modifiedBy = Util.GetUserIdByName(_exportImportJob, other.LastModifiedByUserID, other.LastModifiedByUserName);
                                _dataProvider.UpdateSettingRecordChangers("TabSettings", "TabID",
                                    localTab.TabID, other.SettingName, createdBy, modifiedBy);
                                Result.AddLogEntry("Updated tab setting", other.SettingName);
                            }
                            else
                            {
                                goto case CollisionResolution.Ignore;
                            }
                            break;
                        case CollisionResolution.Ignore:
                            Result.AddLogEntry("Ignored tab setting", other.SettingName);
                            break;
                        default:
                            throw new ArgumentOutOfRangeException(_importDto.CollisionResolution.ToString());
                    }
                }
            }

            return tabSettings.Count;
        }

        private int ImportTabPermissions(TabInfo localTab, ExportTab otherTab, bool isNew)
        {
            var count = 0;
            var tabPermissions = Repository.GetRelatedItems<ExportTabPermission>(otherTab.ReferenceId ?? -1).ToList();
            var localTabPermissions = localTab.TabPermissions.ToList();
            foreach (var other in tabPermissions)
            {
                var local = isNew ? null : localTabPermissions.OfType<TabPermissionInfo>().FirstOrDefault(
                    x => x.PermissionCode == other.PermissionCode &&
                         x.PermissionKey == other.PermissionKey
                         && x.PermissionName == other.PermissionName &&
                         (x.RoleName == other.RoleName || string.IsNullOrEmpty(x.RoleName) && string.IsNullOrEmpty(other.RoleName))
                         &&
                         (x.Username == other.Username || string.IsNullOrEmpty(x.Username) && string.IsNullOrEmpty(other.Username)));

                var isUpdate = false;
                if (local != null)
                {
                    switch (_importDto.CollisionResolution)
                    {
                        case CollisionResolution.Overwrite:
                            isUpdate = true;
                            break;
                        case CollisionResolution.Ignore:
                            Result.AddLogEntry("Ignored tab permission", other.PermissionKey);
                            break;
                        default:
                            throw new ArgumentOutOfRangeException(_importDto.CollisionResolution.ToString());
                    }
                }

                if (isUpdate)
                {
                    //UNDONE: Do we really need to update an existing permission? It won't do anything; permissions are immutable
                    //Result.AddLogEntry("Updated tab permission", other.PermissionKey);
                }
                else
                {
                    local = new TabPermissionInfo
                    {
                        TabID = localTab.TabID,
                        UserID = Util.GetUserIdByName(_exportImportJob, other.UserID, other.Username),
                        Username = other.Username,
                        RoleID = Util.GetRoleIdByName(_importDto.PortalId, other.RoleName) ?? -1, //TODO: is this right? Won't give false role?
                        RoleName = other.RoleName,
                        ModuleDefID = Util.GeModuleDefIdByFriendltName(other.FriendlyName) ?? -1,
                        PermissionKey = other.PermissionKey,
                        AllowAccess = other.AllowAccess,
                        PermissionID = Util.GePermissionIdByName(other.PermissionCode, other.PermissionKey, other.PermissionName) ?? -1,

                    };

                    other.LocalId = localTab.TabPermissions.Add(local);
                    var createdBy = Util.GetUserIdByName(_exportImportJob, other.CreatedByUserID, other.CreatedByUserName);
                    var modifiedBy = Util.GetUserIdByName(_exportImportJob, other.LastModifiedByUserID, other.LastModifiedByUserName);
                    _dataProvider.UpdateRecordChangers("TabPermissions", "TabPermissionID",
                        local.TabPermissionID, createdBy, modifiedBy);

                    Result.AddLogEntry("Added tab permission", other.PermissionKey);
                    count++;
                }
            }

            return count;
        }

        private int ImportTabUrls(TabInfo localTab, ExportTab otherTab, bool isNew)
        {
            var count = 0;
            var tabUrls = Repository.GetRelatedItems<ExportTabUrl>(otherTab.ReferenceId ?? -1).ToList();
            var localUrls = localTab.TabUrls;
            foreach (var other in tabUrls)
            {
                var local = isNew ? null : localUrls.FirstOrDefault(url => url.SeqNum == other.SeqNum);
                if (local != null)
                {
                    switch (_importDto.CollisionResolution)
                    {
                        case CollisionResolution.Overwrite:
                            local.Url = other.Url;

                            TabController.Instance.SaveTabUrl(local, _importDto.PortalId, true);
                            Result.AddLogEntry("Update Tab Url", other.Url);
                            break;
                        case CollisionResolution.Ignore:
                            Result.AddLogEntry("Ignored tab url", other.Url);
                            break;
                        default:
                            throw new ArgumentOutOfRangeException(_importDto.CollisionResolution.ToString());
                    }
                }
                else
                {
                    var alias = PortalAliasController.Instance.GetPortalAliasesByPortalId(_importDto.PortalId).FirstOrDefault(a => a.IsPrimary);
                    local = new TabUrlInfo
                    {
                        TabId = localTab.TabID,
                        CultureCode = other.CultureCode,
                        HttpStatus = other.HttpStatus,
                        IsSystem = other.IsSystem,
                        PortalAliasId = alias?.PortalAliasID ?? -1,
                        PortalAliasUsage = (PortalAliasUsageType)(other.PortalAliasUsage ?? 0), // reset to default
                        QueryString = other.QueryString,
                        SeqNum = other.SeqNum,
                        Url = other.Url,
                    };

                    TabController.Instance.SaveTabUrl(local, _importDto.PortalId, true);

                    //TODO: fix the TabURlInfo primary key then use this one
                    //var createdBy = Util.GetUserIdByName(_importJob, other.CreatedByUserID, other.CreatedByUserName);
                    //var modifiedBy = Util.GetUserIdByName(_importJob, other.LastModifiedByUserID, other.LastModifiedByUserName);
                    //_dataProvider.UpdateRecordChangers("TabUrls", "TabId", local.TabId, createdBy, modifiedBy);

                    Result.AddLogEntry("Added Tab Url", other.Url);
                    Result.AddLogEntry("WARN: Tab alias skin might have different portal alias than intended.", other.HTTPAlias);
                    count++;
                }
            }

            return count;
        }

        private int ImportTabAliasSkins(TabInfo localTab, ExportTab otherTab, bool isNew)
        {
            var count = 0;
            var tabAliasSkins = Repository.GetRelatedItems<ExportTabAliasSkin>(otherTab.ReferenceId ?? -1).ToList();
            var localALiasSkins = localTab.AliasSkins;
            using (var db = DataContext.Instance())
            {
                foreach (var other in tabAliasSkins)
                {
                    var local = isNew ? null : localALiasSkins.FirstOrDefault(alias =>
                        alias.SkinSrc != other.SkinSrc || alias.HttpAlias != other.HTTPAlias);

                    var createdBy = Util.GetUserIdByName(_exportImportJob, other.CreatedByUserID, other.CreatedByUserName);
                    var modifiedBy = Util.GetUserIdByName(_exportImportJob, other.LastModifiedByUserID, other.LastModifiedByUserName);
                    if (local != null)
                    {
                        switch (_importDto.CollisionResolution)
                        {
                            case CollisionResolution.Overwrite:
                                local.SkinSrc = other.SkinSrc;
                                local.HttpAlias = other.HTTPAlias;

                                var aliasSkinCtx = db.GetRepository<TabAliasSkinInfo>();
                                aliasSkinCtx.Update(local);

                                Result.AddLogEntry("Update Tab alias skin", other.SkinSrc);
                                break;
                            case CollisionResolution.Ignore:
                                Result.AddLogEntry("Ignored tab alias skin", other.SkinSrc);
                                break;
                            default:
                                throw new ArgumentOutOfRangeException(_importDto.CollisionResolution.ToString());
                        }
                    }
                    else
                    {
                        var alias = PortalAliasController.Instance.GetPortalAliasesByPortalId(_importDto.PortalId).FirstOrDefault(a => a.IsPrimary);

                        local = new TabAliasSkinInfo
                        {
                            TabId = localTab.TabID,
                            SkinSrc = other.SkinSrc,
                            HttpAlias = other.HTTPAlias,
                            PortalAliasId = alias?.PortalAliasID ?? -1,
                        };

                        var aliasSkinCtx = db.GetRepository<TabAliasSkinInfo>();
                        aliasSkinCtx.Insert(local);
                        //TODO: update changers
                        Result.AddLogEntry("Added Tab alias skin", other.SkinSrc);
                        Result.AddLogEntry("WARN: Tab alias skin might have different portal alias than intended.", other.HTTPAlias);
                        count++;
                    }
                }
            }

            return count;
        }

        private int ImportTabModulesAndRelatedItems(TabInfo localTab, ExportTab otherTab, bool isNew)
        {
            var count = 0;
            var exportedModules = Repository.GetRelatedItems<ExportModule>(otherTab.ReferenceId ?? -1);
            var localModules = isNew ? new List<ExportModule>() : EntitiesController.Instance.GetModules(
                otherTab.TabId, true, Constants.MinDbTime, Constants.MaxDbTime);
            foreach (var other in exportedModules)
            {
                //TODO: how to decide existing when more than one module of same module definition is used.
                var locals = localModules.Where(m => m.FriendlyName == other.FriendlyName).ToList();
                if (locals.Count == 0)
                {
                    var local = new ModuleInfo
                    {
                        TabID = localTab.TabID,
                        ModuleDefID = other.ModuleDefID, //TODO: map to local ID
                        AllTabs = other.AllTabs,
                        IsDeleted = other.IsDeleted,
                        InheritViewPermissions = other.InheritViewPermissions ?? false,
                        StartDate = other.StartDate ?? DateTime.MinValue,
                        EndDate = other.EndDate ?? DateTime.MinValue,
                        PortalID = localTab.PortalID,
                        LastContentModifiedOnDate = other.LastContentModifiedOnDate ?? DateTime.MinValue,
                        ContentItemId = -1, //TODO: what about this?
                        IsShareable = other.IsShareable,
                        IsShareableViewOnly = other.IsShareableViewOnly,
                    };
                    other.LocalId = _moduleController.AddModule(local); //TODO: is this the right call?

                    var createdBy = Util.GetUserIdByName(_exportImportJob, other.CreatedByUserID, other.CreatedByUserName);
                    var modifiedBy = Util.GetUserIdByName(_exportImportJob, other.LastModifiedByUserID, other.LastModifiedByUserName);
                    _dataProvider.UpdateRecordChangers("Modules", "ModuleID", local.ModuleID, createdBy, modifiedBy);

                    _totals.TotalModuleSettings += ImportModuleSettings(local, other, isNew);
                    _totals.TotalPermissions += ImportModulePermissions(local, other, isNew);

                    if (_exportDto.IncludeContent)
                    {
                        _totals.TotalContents += ImportPortableContent(localTab.TabID, local, other, isNew);
                    }

                    Result.AddLogEntry("Added module", local.ModuleID.ToString());
                }
                else
                {
                    foreach (var item in locals)
                    {
                        var local = _moduleController.GetModule(item.ModuleID, localTab.TabID, false);
                        local.AllTabs = other.AllTabs;
                        local.IsDeleted = other.IsDeleted;
                        local.InheritViewPermissions = other.InheritViewPermissions ?? false;
                        local.StartDate = other.StartDate ?? DateTime.MinValue;
                        local.EndDate = other.EndDate ?? DateTime.MinValue;
                        local.PortalID = localTab.PortalID;
                        local.LastContentModifiedOnDate = other.LastContentModifiedOnDate ?? DateTime.MinValue;
                        //local.ContentItemId = -1; //TODO: what to do for this?
                        local.IsShareable = other.IsShareable;
                        local.IsShareableViewOnly = other.IsShareableViewOnly;
                        _moduleController.UpdateModule(local); //TODO: is this the right call?

                        var createdBy = Util.GetUserIdByName(_exportImportJob, other.CreatedByUserID, other.CreatedByUserName);
                        var modifiedBy = Util.GetUserIdByName(_exportImportJob, other.LastModifiedByUserID, other.LastModifiedByUserName);
                        _dataProvider.UpdateRecordChangers("Modules", "ModuleID", item.ModuleID, createdBy, modifiedBy);

                        _totals.TotalModuleSettings += ImportModuleSettings(local, other, isNew);
                        _totals.TotalPermissions += ImportModulePermissions(local, other, isNew);

                        if (_exportDto.IncludeContent)
                        {
                            _totals.TotalContents += ImportPortableContent(localTab.TabID, local, other, isNew);
                        }
                        Result.AddLogEntry("Updated module", local.ModuleID.ToString());
                    }
                }

                count++;
            }

            return count;
        }

        private int ImportModuleSettings(ModuleInfo localModule, ExportModule otherModule, bool isNew)
        {
            var count = 0;
            var moduleSettings = Repository.GetRelatedItems<ExportModuleSetting>(otherModule.ReferenceId ?? -1).ToList();
            foreach (var other in moduleSettings)
            {
                var localValue = isNew ? string.Empty : localModule.ModuleSettings[other.SettingName].ToString();
                if (string.IsNullOrEmpty(localValue))
                {
                    _moduleController.UpdateModuleSetting(localModule.ModuleID, other.SettingName, other.SettingValue);
                    var createdBy = Util.GetUserIdByName(_exportImportJob, other.CreatedByUserID, other.CreatedByUserName);
                    var modifiedBy = Util.GetUserIdByName(_exportImportJob, other.LastModifiedByUserID, other.LastModifiedByUserName);
                    _dataProvider.UpdateSettingRecordChangers("ModuleSettings", "ModuleID",
                        localModule.ModuleID, other.SettingName, createdBy, modifiedBy);
                    Result.AddLogEntry("Added module setting", other.SettingName);
                    count++;
                }
                else
                {
                    switch (_importDto.CollisionResolution)
                    {
                        case CollisionResolution.Overwrite:
                            if (localValue != other.SettingValue)
                            {
                                // the next will clear the cache
                                _moduleController.UpdateModuleSetting(localModule.ModuleID, other.SettingName, other.SettingValue);
                                var createdBy = Util.GetUserIdByName(_exportImportJob, other.CreatedByUserID, other.CreatedByUserName);
                                var modifiedBy = Util.GetUserIdByName(_exportImportJob, other.LastModifiedByUserID, other.LastModifiedByUserName);
                                _dataProvider.UpdateSettingRecordChangers("ModuleSettings", "ModuleID",
                                    localModule.ModuleID, other.SettingName, createdBy, modifiedBy);
                                Result.AddLogEntry("Updated module setting", other.SettingName);
                                count++;
                            }
                            else
                            {
                                goto case CollisionResolution.Ignore;
                            }
                            break;
                        case CollisionResolution.Ignore:
                            Result.AddLogEntry("Ignored module setting", other.SettingName);
                            break;
                        default:
                            throw new ArgumentOutOfRangeException(_importDto.CollisionResolution.ToString());
                    }
                }
            }

            return count;
        }

        private int ImportModulePermissions(ModuleInfo localModule, ExportModule otherModule, bool isNew)
        {
            var count = 0;
            var modulePermissions = Repository.GetRelatedItems<ExportModulePermission>(otherModule.ReferenceId ?? -1).ToList();
            var localModulePermissions = isNew
                ? new List<ModulePermissionInfo>()
                : localModule.ModulePermissions.OfType<ModulePermissionInfo>().ToList();
            foreach (var other in modulePermissions)
            {
                var local = localModulePermissions.FirstOrDefault(
                    x => x.PermissionCode == other.PermissionCode &&
                         x.PermissionKey == other.PermissionKey
                         && x.PermissionName == other.PermissionName &&
                         (x.RoleName == other.RoleName || string.IsNullOrEmpty(x.RoleName) && string.IsNullOrEmpty(other.RoleName))
                         &&
                         (x.Username == other.Username || string.IsNullOrEmpty(x.Username) && string.IsNullOrEmpty(other.Username)));

                var isUpdate = false;
                if (local != null)
                {
                    switch (_importDto.CollisionResolution)
                    {
                        case CollisionResolution.Overwrite:
                            isUpdate = true;
                            break;
                        case CollisionResolution.Ignore:
                            Result.AddLogEntry("Ignored tab permission", other.PermissionKey);
                            break;
                        default:
                            throw new ArgumentOutOfRangeException(_importDto.CollisionResolution.ToString());
                    }
                }

                if (isUpdate)
                {
                    //UNDONE: Do we really need to update an existing permission? It won't do anything; permissions are immutable
                    //Result.AddLogEntry("Updated tab permission", other.PermissionKey);
                }
                else
                {
                    local = new ModulePermissionInfo
                    {
                        ModuleID = localModule.ModuleID,
                        RoleID = Util.GetRoleIdByName(_importDto.PortalId, other.RoleName) ?? -1, //TODO: is this right? Won't give false role?
                        RoleName = other.RoleName,
                        UserID = Util.GetUserIdByName(_exportImportJob, other.UserID, other.Username),
                        Username = other.Username,
                        PermissionKey = other.PermissionKey,
                        AllowAccess = other.AllowAccess,
                        PermissionID = Util.GePermissionIdByName(other.PermissionCode, other.PermissionKey, other.PermissionName) ?? -1,
                    };

                    other.LocalId = localModule.ModulePermissions.Add(local);
                    var createdBy = Util.GetUserIdByName(_exportImportJob, other.CreatedByUserID, other.CreatedByUserName);
                    var modifiedBy = Util.GetUserIdByName(_exportImportJob, other.LastModifiedByUserID, other.LastModifiedByUserName);
                    _dataProvider.UpdateRecordChangers("ModulePermissions", "ModulePermissionID",
                        local.ModulePermissionID, createdBy, modifiedBy);

                    Result.AddLogEntry("Added module permission", other.PermissionKey);
                    count++;
                }
            }

            return count;
        }

        private int ImportPortableContent(int tabId, ModuleInfo localModule, ExportModule otherModule, bool isNew)
        {
            var moduleDef = ModuleDefinitionController.GetModuleDefinitionByID(localModule.ModuleDefID);
            var desktopModuleInfo = DesktopModuleController.GetDesktopModule(moduleDef.DesktopModuleID, _exportDto.PortalId);
            if (!string.IsNullOrEmpty(desktopModuleInfo?.BusinessControllerClass))
            {
                try
                {
                    var module = _moduleController.GetModule(localModule.ModuleID, tabId, true);
                    if (!string.IsNullOrEmpty(module.DesktopModule.BusinessControllerClass) && module.DesktopModule.IsPortable)
                    {
                        var businessController = Reflection.CreateObject(module.DesktopModule.BusinessControllerClass, module.DesktopModule.BusinessControllerClass);
                        var controller = businessController as IPortable;
                        if (controller != null)
                        {
                            //Note: there is no chek whether the content exists or not to manage conflict resolution
                            if (isNew || _importDto.CollisionResolution == CollisionResolution.Overwrite)
                            {
                                var exportedContent = Repository.GetRelatedItems<ExportModuleContent>(otherModule.ReferenceId ?? -1);
                                var version = DotNetNukeContext.Current.Application.Version.ToString(3);
                                foreach (var moduleContent in exportedContent)
                                {
                                    controller.ImportModule(localModule.ModuleID, moduleContent.XmlContent, version, _exportImportJob.CreatedByUserId);
                                }
                                Result.AddLogEntry("Inserted/Updated module content:", localModule.ModuleID.ToString());
                                return 1;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Result.AddLogEntry("Error cerating business class type", desktopModuleInfo.BusinessControllerClass);
                    Logger.Error("Error cerating business class type. " + ex);
                }
            }
            return 0;
        }

        private int ImportTabModules(TabInfo localTab, ExportTab otherTab, bool isNew)
        {
            var count = 0;
            var exportedTabModules = Repository.GetRelatedItems<ExportTabModule>(otherTab.ReferenceId ?? -1);
            var localModules = isNew
                ? new List<ModuleInfo>()
                : _moduleController.GetTabModules(localTab.TabID).Values.ToList();
            foreach (var other in exportedTabModules)
            {
                var createdBy = Util.GetUserIdByName(_exportImportJob, other.CreatedByUserID, other.CreatedByUserName);
                var modifiedBy = Util.GetUserIdByName(_exportImportJob, other.LastModifiedByUserID, other.LastModifiedByUserName);

                //TODO: how to decide existing when more than one module of same module definition is used.
                var locals = localModules.Where(m => m.DesktopModule.FriendlyName == other.FriendlyName).ToList();
                if (locals.Count == 0)
                {
                    var local = new ModuleInfo()
                    {
                        TabID = other.TabID,
                        ModuleID = other.ModuleID,
                        PaneName = other.PaneName,
                        ModuleOrder = other.ModuleOrder,
                        CacheTime = other.CacheTime,
                        Alignment = other.Alignment,
                        Color = other.Color,
                        Border = other.Border,
                        IconFile = other.IconFile,
                        Visibility = (VisibilityState)other.Visibility,
                        ContainerSrc = other.ContainerSrc,
                        DisplayTitle = other.DisplayTitle,
                        DisplayPrint = other.DisplayPrint,
                        DisplaySyndicate = other.DisplaySyndicate,
                        IsWebSlice = other.IsWebSlice,
                        WebSliceTitle = other.WebSliceTitle,
                        WebSliceExpiryDate = other.WebSliceExpiryDate ?? DateTime.MinValue,
                        WebSliceTTL = other.WebSliceTTL ?? -1,
                        IsDeleted = other.IsDeleted,
                        CacheMethod = other.CacheMethod,
                        ModuleTitle = other.ModuleTitle,
                        Header = other.Header,
                        Footer = other.Footer,
                        CultureCode = other.CultureCode,
                        UniqueId = other.UniqueId,
                        VersionGuid = other.VersionGuid,
                        DefaultLanguageGuid = other.DefaultLanguageGuid ?? Guid.Empty,
                        LocalizedVersionGuid = other.LocalizedVersionGuid,
                        ContentItemId = -1, //TODO: what about this?
                    };

                    other.LocalId = _moduleController.AddModule(local); //TODO: is this the right call?
                    _dataProvider.UpdateRecordChangers("TabModules", "TabModuleID", local.ModuleID, createdBy, modifiedBy);
                    _totals.TotalTabModuleSettings += ImportTabModuleSettings(local, other, isNew);
                    Result.AddLogEntry("Added tab module", local.TabModuleID.ToString());
                }
                else
                {
                    foreach (var local in locals)
                    {
                        local.TabID = localTab.TabID;
                        local.ModuleID = other.ModuleID;
                        local.PaneName = other.PaneName;
                        local.ModuleOrder = other.ModuleOrder;
                        local.CacheTime = other.CacheTime;
                        local.Alignment = other.Alignment;
                        local.Color = other.Color;
                        local.Border = other.Border;
                        local.IconFile = other.IconFile;
                        local.Visibility = (VisibilityState)other.Visibility;
                        local.ContainerSrc = other.ContainerSrc;
                        local.DisplayTitle = other.DisplayTitle;
                        local.DisplayPrint = other.DisplayPrint;
                        local.DisplaySyndicate = other.DisplaySyndicate;
                        local.IsWebSlice = other.IsWebSlice;
                        local.WebSliceTitle = other.WebSliceTitle;
                        local.WebSliceExpiryDate = other.WebSliceExpiryDate ?? DateTime.MinValue;
                        local.WebSliceTTL = other.WebSliceTTL ?? -1;
                        local.IsDeleted = other.IsDeleted;
                        local.CacheMethod = other.CacheMethod;
                        local.ModuleTitle = other.ModuleTitle;
                        local.Header = other.Header;
                        local.Footer = other.Footer;
                        local.CultureCode = other.CultureCode;
                        local.UniqueId = other.UniqueId;
                        local.VersionGuid = other.VersionGuid;
                        local.DefaultLanguageGuid = other.DefaultLanguageGuid ?? Guid.Empty;
                        local.LocalizedVersionGuid = other.LocalizedVersionGuid;

                        _moduleController.UpdateModule(local); //TODO: is this the right call?
                        _dataProvider.UpdateRecordChangers("TabModules", "TabModuleID", local.ModuleID, createdBy, modifiedBy);
                        _totals.TotalTabModuleSettings += ImportTabModuleSettings(local, other, isNew);
                        Result.AddLogEntry("Updated tab module", local.TabModuleID.ToString());
                    }
                }

                count++;
            }

            return count;
        }

        private int ImportTabModuleSettings(ModuleInfo localTabModule, ExportTabModule otherTabModule, bool isNew)
        {
            var count = 0;
            var tabModuleSettings = Repository.GetRelatedItems<ExportTabModuleSetting>(otherTabModule.ReferenceId ?? -1).ToList();
            foreach (var other in tabModuleSettings)
            {
                var localValue = isNew ? string.Empty : localTabModule.TabModuleSettings[other.SettingName].ToString();
                if (string.IsNullOrEmpty(localValue))
                {
                    // the next will clear the cache
                    _moduleController.UpdateTabModuleSetting(localTabModule.ModuleID, other.SettingName, other.SettingValue);
                    var createdBy = Util.GetUserIdByName(_exportImportJob, other.CreatedByUserID, other.CreatedByUserName);
                    var modifiedBy = Util.GetUserIdByName(_exportImportJob, other.LastModifiedByUserID, other.LastModifiedByUserName);
                    _dataProvider.UpdateSettingRecordChangers("TabModuleSettings", "TabModuleID",
                        localTabModule.TabModuleID, other.SettingName, createdBy, modifiedBy);
                    Result.AddLogEntry("Added tab module setting", other.SettingName);
                    count++;
                }
                else
                {
                    switch (_importDto.CollisionResolution)
                    {
                        case CollisionResolution.Overwrite:
                            if (localValue != other.SettingValue)
                            {
                                // the next will clear the cache
                                _moduleController.UpdateTabModuleSetting(localTabModule.ModuleID, other.SettingName, other.SettingValue);
                                var createdBy = Util.GetUserIdByName(_exportImportJob, other.CreatedByUserID, other.CreatedByUserName);
                                var modifiedBy = Util.GetUserIdByName(_exportImportJob, other.LastModifiedByUserID, other.LastModifiedByUserName);
                                _dataProvider.UpdateSettingRecordChangers("TabModuleSettings", "TabModuleID",
                                    localTabModule.TabModuleID, other.SettingName, createdBy, modifiedBy);
                                Result.AddLogEntry("Updated tab module setting", other.SettingName);
                                count++;
                            }
                            else
                            {
                                goto case CollisionResolution.Ignore;
                            }
                            break;
                        case CollisionResolution.Ignore:
                            Result.AddLogEntry("Ignored module setting", other.SettingName);
                            break;
                        default:
                            throw new ArgumentOutOfRangeException(_importDto.CollisionResolution.ToString());
                    }
                }
            }

            return count;
        }

        private static int GetParentLocalTabId(int otherTabId, IEnumerable<ExportTab> exportedTabs, IEnumerable<TabInfo> localTabs)
        {
            var otherTab = exportedTabs.FirstOrDefault(t => t.TabId == otherTabId);
            if (otherTab != null)
            {
                var localTab = localTabs.FirstOrDefault(t => t.TabName == otherTab.TabName && t.TabPath == otherTab.TabPath);
                if (localTab != null)
                    return localTab.TabID;
            }

            return -1;
        }

        private static void SetTabData(TabInfo localTab, ExportTab otherTab)
        {
            localTab.TabOrder = otherTab.TabOrder;
            localTab.TabName = otherTab.TabName;
            localTab.IsVisible = otherTab.IsVisible;
            localTab.ParentId = otherTab.ParentId ?? -1;
            localTab.IconFile = otherTab.IconFile;
            localTab.DisableLink = otherTab.DisableLink;
            localTab.Title = otherTab.Title;
            localTab.Description = otherTab.Description;
            localTab.KeyWords = otherTab.KeyWords;
            localTab.IsDeleted = otherTab.IsDeleted;
            localTab.Url = otherTab.Url;
            localTab.SkinSrc = otherTab.SkinSrc;
            localTab.ContainerSrc = otherTab.ContainerSrc;
            localTab.StartDate = otherTab.StartDate ?? DateTime.MinValue;
            localTab.EndDate = otherTab.EndDate ?? DateTime.MinValue;
            localTab.RefreshInterval = otherTab.RefreshInterval ?? -1;
            localTab.PageHeadText = otherTab.PageHeadText;
            localTab.IsSecure = otherTab.IsSecure;
            localTab.PermanentRedirect = otherTab.PermanentRedirect;
            localTab.SiteMapPriority = otherTab.SiteMapPriority;
            localTab.IconFileLarge = otherTab.IconFileLarge;
            localTab.CultureCode = otherTab.CultureCode;
            //localTab.ContentItemID = otherTab.ContentItemID ?? -1;  //TODO: what to set here?
            localTab.UniqueId = otherTab.UniqueId;
            localTab.VersionGuid = otherTab.VersionGuid;
            localTab.DefaultLanguageGuid = otherTab.DefaultLanguageGuid ?? Guid.Empty;
            localTab.LocalizedVersionGuid = otherTab.LocalizedVersionGuid;
            localTab.Level = otherTab.Level;
            localTab.TabPath = otherTab.TabPath;
            localTab.HasBeenPublished = otherTab.HasBeenPublished;
            localTab.IsSystem = otherTab.IsSystem;
        }

        #region Methods for updating CreatedBy and ModifiedBy of various tables

        private void UpdateTabChangers(int tabId, int createdBy, int modifiedBy)
        {
            _dataProvider.UpdateRecordChangers("Tabs", "TabID", tabId, createdBy, modifiedBy);
        }

        // ReSharper disable UnusedMember.Local
        private void UpdateTabPermissionChangers(int tabPermissionId, int createdBy, int modifiedBy)
        {
            _dataProvider.UpdateRecordChangers("TabPermissions", "TabPermissionID", tabPermissionId, createdBy, modifiedBy);
        }

        private void UpdateTabSettingChangers(int tabId, string settingName, int createdBy, int modifiedBy)
        {
            _dataProvider.UpdateSettingRecordChangers("TabSettings", "TabID", tabId, settingName, createdBy, modifiedBy);
        }

        private void UpdateTabUrlChangers(int tabUrlId, int createdBy, int modifiedBy)
        {
            _dataProvider.UpdateRecordChangers("TabUrls", "TabUrlID", tabUrlId, createdBy, modifiedBy);
        }

        private void UpdateTabAliasSkinChangers(int tabId, int createdBy, int modifiedBy)
        {
            _dataProvider.UpdateRecordChangers("TabAliasSkins", "TabAliasSkinID", tabId, createdBy, modifiedBy);
        }

        private void UpdateTabModuleChangers(int tabModuleId, int createdBy, int modifiedBy)
        {
            _dataProvider.UpdateRecordChangers("TabModules", "TabModuleID", tabModuleId, createdBy, modifiedBy);
        }

        private void UpdateTabModuleSettingsChangers(int tabModuleId, string settingName, int createdBy, int modifiedBy)
        {
            _dataProvider.UpdateSettingRecordChangers("TabModuleSettings", "TabModuleID", tabModuleId, settingName, createdBy, modifiedBy);
        }

        private void UpdateModuleChangers(int moduleId, int createdBy, int modifiedBy)
        {
            _dataProvider.UpdateRecordChangers("Modules", "ModuleID", moduleId, createdBy, modifiedBy);
        }

        private void UpdateModulePermissionChangers(int modulePermissionId, int createdBy, int modifiedBy)
        {
            _dataProvider.UpdateRecordChangers("ModulePermissions", "ModulePermissionID", modulePermissionId, createdBy, modifiedBy);
        }

        private void UpdateModuleSettingsChangers(int moduleId, string settingName, int createdBy, int modifiedBy)
        {
            _dataProvider.UpdateSettingRecordChangers("ModuleSettings", "ModuleID", moduleId, settingName, createdBy, modifiedBy);
        }

        #endregion

        #endregion

        #region export methods

        private void ProcessExportPages()
        {
            var selectedPages = _exportDto.Pages;
            _totals = string.IsNullOrEmpty(CheckPoint.StageData)
                ? new ProgressTotals()
                : JsonConvert.DeserializeObject<ProgressTotals>(CheckPoint.StageData);

            var portalId = _exportImportJob.PortalId;

            var toDate = _exportImportJob.CreatedOnDate.ToLocalTime();
            var fromDate = _exportDto.FromDate?.DateTime.ToLocalTime();
            var isAllIncluded = selectedPages.Any(p => p.TabId == -1);

            var allTabs = EntitiesController.Instance.GetPortalTabs(portalId,
                _exportDto.IncludeDeletions, IncludeSystem, toDate, fromDate); // ordered by TabID

            //Update the total items count in the check points. This should be updated only once.
            CheckPoint.TotalItems = CheckPoint.TotalItems <= 0 ? allTabs.Count : CheckPoint.TotalItems;
            if (CheckPointStageCallback(this)) return;
            var progressStep = 100.0 / allTabs.Count;

            //Note: We assume child tabs have bigger TabID values for restarting from checkpoints.
            //      Anything other than this might not work properly with shedule restarts.
            foreach (var otherPg in allTabs)
            {
                if (CheckCancelled(_exportImportJob)) break;

                if (_totals.LastProcessedId > otherPg.TabID) continue;

                var tab = _tabController.GetTab(otherPg.TabID, portalId);
                if (isAllIncluded || IsTabIncluded(otherPg, allTabs, selectedPages))
                {
                    var exportPage = SaveExportPage(tab);

                    _totals.TotalSettings +=
                        ExportTabSettings(exportPage, toDate, fromDate);

                    _totals.TotalPermissions +=
                        ExportTabPermissions(exportPage, toDate, fromDate);

                    _totals.TotalUrls +=
                        ExportTabUrls(exportPage, toDate, fromDate);

                    _totals.TotalAliasSkins +=
                        ExportTabAliasSkins(exportPage, toDate, fromDate);

                    _totals.TotalModules +=
                        ExportTabModulesAndRelatedItems(exportPage, toDate, fromDate);

                    _totals.TotalTabModules +=
                        ExportTabModules(exportPage, _exportDto.IncludeDeletions, toDate, fromDate);

                    _totals.TotalTabModuleSettings +=
                        ExportTabModuleSettings(exportPage, toDate, fromDate);

                    _totals.TotalTabs++;
                    _totals.LastProcessedId = tab.TabID;
                }

                CheckPoint.Progress += progressStep;
                CheckPoint.ProcessedItems++;
                CheckPoint.StageData = JsonConvert.SerializeObject(_totals);
                if (CheckPointStageCallback(this)) break;
            }

            ReportExportTotals();
        }

        private int ExportTabSettings(ExportTab exportPage, DateTime toDate, DateTime? fromDate)
        {
            var tabSettings = EntitiesController.Instance.GetTabSettings(exportPage.TabId, toDate, fromDate);
            if (tabSettings.Count > 0)
                Repository.CreateItems(tabSettings, exportPage.ReferenceId);
            return tabSettings.Count;
        }

        private int ExportTabPermissions(ExportTab exportPage, DateTime toDate, DateTime? fromDate)
        {
            var tabPermissions = EntitiesController.Instance.GetTabPermissions(exportPage.TabId, toDate, fromDate);
            if (tabPermissions.Count > 0)
                Repository.CreateItems(tabPermissions, exportPage.ReferenceId);
            return tabPermissions.Count;
        }

        private int ExportTabUrls(ExportTab exportPage, DateTime toDate, DateTime? fromDate)
        {
            var tabUrls = EntitiesController.Instance.GetTabUrls(exportPage.TabId, toDate, fromDate);
            if (tabUrls.Count > 0)
                Repository.CreateItems(tabUrls, exportPage.ReferenceId);
            return tabUrls.Count;
        }

        private int ExportTabAliasSkins(ExportTab exportPage, DateTime toDate, DateTime? fromDate)
        {
            var tabSkins = EntitiesController.Instance.GetTabAliasSkins(exportPage.TabId, toDate, fromDate);
            if (tabSkins.Count > 0)
                Repository.CreateItems(tabSkins, exportPage.ReferenceId);
            return tabSkins.Count;
        }

        private int ExportTabModules(ExportTab exportPage, bool includeDeleted, DateTime toDate, DateTime? fromDate)
        {
            var tabModules = EntitiesController.Instance.GetTabModules(exportPage.TabId, includeDeleted, toDate, fromDate);
            if (tabModules.Count > 0)
                Repository.CreateItems(tabModules, exportPage.ReferenceId);
            return tabModules.Count;
        }

        private int ExportTabModuleSettings(ExportTab exportPage, DateTime toDate, DateTime? fromDate)
        {
            var tabModuleSettings = EntitiesController.Instance.GetTabModuleSettings(exportPage.TabId, toDate, fromDate);
            if (tabModuleSettings.Count > 0)
                Repository.CreateItems(tabModuleSettings, exportPage.ReferenceId);
            return tabModuleSettings.Count;
        }

        private int ExportTabModulesAndRelatedItems(ExportTab exportPage, DateTime toDate, DateTime? fromDate)
        {
            var modules = EntitiesController.Instance.GetModules(exportPage.TabId, _exportDto.IncludeDeletions, toDate, fromDate);
            if (modules.Count > 0)
            {
                Repository.CreateItems(modules, exportPage.ReferenceId);
                foreach (var exportModule in modules)
                {
                    _totals.TotalModuleSettings +=
                        ExportModuleSettings(exportModule, toDate, fromDate);

                    _totals.TotalPermissions +=
                        ExportModulePermissions(exportModule, toDate, fromDate);

                    if (_exportDto.IncludeContent)
                    {
                        _totals.TotalContents +=
                            ExportPortableContent(exportPage, exportModule, toDate, fromDate);
                    }
                }
            }

            return modules.Count;
        }

        private int ExportModuleSettings(ExportModule exportModule, DateTime toDate, DateTime? fromDate)
        {
            var moduleSettings = EntitiesController.Instance.GetModuleSettings(exportModule.ModuleID, toDate, fromDate);
            if (moduleSettings.Count > 0)
                Repository.CreateItems(moduleSettings, exportModule.ReferenceId);
            return moduleSettings.Count;
        }

        private int ExportModulePermissions(ExportModule exportModule, DateTime toDate, DateTime? fromDate)
        {
            var modulePermission = EntitiesController.Instance.GetModulePermissions(exportModule.ModuleID, toDate, fromDate);
            if (modulePermission.Count > 0)
                Repository.CreateItems(modulePermission, exportModule.ReferenceId);
            return modulePermission.Count;
        }

        // ReSharper disable UnusedParameter.Local
        private int ExportPortableContent(ExportTab exportPage, ExportModule exportModule, DateTime toDate, DateTime? fromDat)
        // ReSharper enable UnusedParameter.Local
        {
            //Note: until now there is no use of time range for content
            var moduleDef = ModuleDefinitionController.GetModuleDefinitionByID(exportModule.ModuleDefID);
            var desktopModuleInfo = DesktopModuleController.GetDesktopModule(moduleDef.DesktopModuleID, _exportDto.PortalId);
            if (!string.IsNullOrEmpty(desktopModuleInfo?.BusinessControllerClass))
            {
                try
                {
                    var module = _moduleController.GetModule(exportModule.ModuleID, exportPage.TabId, true);
                    if (!string.IsNullOrEmpty(module.DesktopModule.BusinessControllerClass) && module.DesktopModule.IsPortable)
                    {
                        var businessController = Reflection.CreateObject(module.DesktopModule.BusinessControllerClass, module.DesktopModule.BusinessControllerClass);
                        var controller = businessController as IPortable;
                        if (controller != null)
                        {
                            var content = Convert.ToString(controller.ExportModule(module.ModuleID));
                            if (!string.IsNullOrEmpty(content))
                            {
                                var record = new ExportModuleContent
                                {
                                    ModuleID = exportModule.ModuleID,
                                    ModuleDefID = exportModule.ModuleDefID,
                                    XmlContent = content,
                                    ReferenceId = exportModule.Id,
                                };
                                Repository.AddSingleItem(record);
                                return 1;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Result.AddLogEntry("Error cerating business class type", desktopModuleInfo.BusinessControllerClass);
                    Logger.Error("Error cerating business class type. " + ex);
                }
            }
            return 0;
        }

        private ExportTab SaveExportPage(TabInfo tab)
        {
            var exportPage = new ExportTab
            {
                TabId = tab.TabID,
                TabOrder = tab.TabOrder,
                TabName = tab.TabName,
                IsVisible = tab.IsVisible,
                ParentId = tab.ParentId <= 0 ? null : (int?)tab.ParentId,
                IconFile = tab.IconFile,
                DisableLink = tab.DisableLink,
                Title = tab.Title,
                Description = tab.Description,
                KeyWords = tab.KeyWords,
                IsDeleted = tab.IsDeleted,
                Url = tab.Url,
                ContainerSrc = tab.ContainerSrc,
                StartDate = tab.StartDate == DateTime.MinValue ? null : (DateTime?)tab.StartDate,
                EndDate = tab.EndDate == DateTime.MinValue ? null : (DateTime?)tab.EndDate,
                RefreshInterval = tab.RefreshInterval <= 0 ? null : (int?)tab.RefreshInterval,
                PageHeadText = tab.PageHeadText,
                IsSecure = tab.IsSecure,
                PermanentRedirect = tab.PermanentRedirect,
                SiteMapPriority = tab.SiteMapPriority,
                CreatedByUserID = tab.CreatedByUserID <= 0 ? null : (int?)tab.CreatedByUserID,
                CreatedOnDate = tab.CreatedOnDate == DateTime.MinValue ? null : (DateTime?)tab.CreatedOnDate,
                LastModifiedByUserID = tab.LastModifiedByUserID <= 0 ? null : (int?)tab.LastModifiedByUserID,
                LastModifiedOnDate = tab.LastModifiedOnDate == DateTime.MinValue ? null : (DateTime?)tab.LastModifiedOnDate,
                IconFileLarge = tab.IconFileLarge,
                CultureCode = tab.CultureCode,
                ContentItemID = tab.ContentItemId < 0 ? null : (int?)tab.ContentItemId,
                UniqueId = tab.UniqueId,
                VersionGuid = tab.VersionGuid,
                DefaultLanguageGuid = tab.DefaultLanguageGuid == Guid.Empty ? null : (Guid?)tab.DefaultLanguageGuid,
                LocalizedVersionGuid = tab.LocalizedVersionGuid,
                Level = tab.Level,
                TabPath = tab.TabPath,
                HasBeenPublished = tab.HasBeenPublished,
                IsSystem = tab.IsSystem,
            };
            Repository.CreateItem(exportPage, null);
            Result.AddLogEntry("Exported page", tab.TabName + " (" + tab.TabPath + ")");
            return exportPage;
        }

        #endregion

        #region helper methods

        private static bool IsTabIncluded(ExportTabInfo tab, IList<ExportTabInfo> allTabs, PageToExport[] selectedPages)
        {
            var first = true;
            do
            {
                var pg = selectedPages.FirstOrDefault(p => p.TabId == tab.TabID);
                if (pg != null)
                {
                    // this is the current page or a parent page for the one we are checking for.
                    if (pg.CheckedState == TriCheckedState.UnChecked)
                        return false;

                    // this is the current page or a parent page for the one we are checking for.
                    // it must be fully checked
                    if (pg.CheckedState == TriCheckedState.Checked)
                        return true;

                    // this is the current page we are checking for and it is partially checked.
                    if (first)
                        return true;
                }

                first = false;
                tab = allTabs.FirstOrDefault(t => t.TabID == tab.ParentID);
            } while (tab != null);

            return false;
        }

        private void ReportExportTotals()
        {
            ReportTotals("Exported");
        }

        private void ReportImportTotals()
        {
            ReportTotals("Imported");
        }

        private void ReportTotals(string prefix)
        {
            Result.AddSummary(prefix + " Tabs", _totals.TotalTabs.ToString());
            Result.AddLogEntry(prefix + " Tab Settings", _totals.TotalSettings.ToString());
            Result.AddLogEntry(prefix + " Tab Permissions", _totals.TotalPermissions.ToString());
            Result.AddLogEntry(prefix + " Tab Urls", _totals.TotalUrls.ToString());
            Result.AddLogEntry(prefix + " Tab Alias Skins", _totals.TotalAliasSkins.ToString());
            Result.AddLogEntry(prefix + " Modules", _totals.TotalModules.ToString());
            Result.AddLogEntry(prefix + " Module Settings", _totals.TotalModuleSettings.ToString());
            Result.AddLogEntry(prefix + " Module Permissions", _totals.TotalModulePermissions.ToString());
            Result.AddLogEntry(prefix + " Tab Modules", _totals.TotalTabModules.ToString());
            Result.AddLogEntry(prefix + " Tab Module Settings", _totals.TotalTabModuleSettings.ToString());
        }

        #endregion

        #region private classes

        [JsonObject]
        public class ProgressTotals
        {
            // for Export: this is the TabID
            // for Import: this is the exported DB row ID; not the TabID
            public int LastProcessedId { get; set; }

            public int TotalTabs { get; set; }
            public int TotalSettings { get; set; }
            public int TotalPermissions { get; set; }
            public int TotalUrls { get; set; }
            public int TotalAliasSkins { get; set; }

            public int TotalModules { get; set; }
            public int TotalModulePermissions { get; set; }
            public int TotalModuleSettings { get; set; }
            public int TotalContents { get; set; }

            public int TotalTabModules { get; set; }
            public int TotalTabModuleSettings { get; set; }
        }
        #endregion
    }
}