﻿using System;
using System.Linq;
using System.Collections.Generic;
using System.Collections;


namespace FilterExtensions
{
    using UnityEngine;
    using Utility;
    using ConfigNodes;

    [KSPAddon(KSPAddon.Startup.MainMenu, true)]
    public class Core : MonoBehaviour
    {
        private static Core instance;

        // storing categories/subCategories loaded at Main Menu for creation when entering SPH/VAB
        internal List<customCategory> Categories = new List<customCategory>();
        internal List<customSubCategory> subCategories = new List<customSubCategory>();

        // mod folder for each part by internal name
        public static Dictionary<string, string> partFolderDict = new Dictionary<string, string>();

        // entry for each unique combination of propellants
        public static List<List<string>> propellantCombos = new List<List<string>>();

        // store all the "All parts" subcategories until all subcategories have been processed
        internal Dictionary<string, customSubCategory> categoryAllSub = new Dictionary<string, customSubCategory>(); // store the config node for the "all" subcategories until all filters have been added

        // state is set on initialisation starting and finishing. This way we know whether a problem was encountered and if it was a problem related to FE
        internal static int state = 0; // 0 = we haven't started yet, 1 = processing started, -1 = processing finished, 2 = processing reattempted

        // Dictionary of icons created on entering the main menu
        public static Dictionary<string, PartCategorizer.Icon> iconDict = new Dictionary<string, PartCategorizer.Icon>();

        public static Core Instance // Reminder to self, don't be abusing static
        {
            get
            {
                return instance;
            }
        }

        void Awake()
        {
            instance = this;
            Log("Version 1.16");

            // Add event for when the Editor GUI becomes active. This is never removed because we need it to fire every time
            GameEvents.onGUIEditorToolbarReady.Add(editor);

            // generate the associations between parts and folders, create all the mod categories, get all propellant combinations
            associateParts();

            // mod categories key: title, value: folder
            // used for adding the folder check to subCategories
            Dictionary<string, string> folderToCategoryDict = new Dictionary<string, string>();
            // load all category configs
            foreach (ConfigNode node in GameDatabase.Instance.GetConfigNodes("CATEGORY"))
            {
                customCategory C = new customCategory(node);
                if (Categories.Find(n => n.categoryName == C.categoryName) == null)
                {
                    Categories.Add(C);
                    if (C.value != null)
                    {
                        if (!folderToCategoryDict.ContainsKey(C.categoryName))
                            folderToCategoryDict.Add(C.categoryName, C.value.Trim());
                    }
                }
            }

            List<customSubCategory> editList = new List<customSubCategory>();
            // load all subCategory configs
            foreach (ConfigNode node in GameDatabase.Instance.GetConfigNodes("SUBCATEGORY"))
            {
                // if multiple categories are specified, create multiple subCategories
                string[] categories = node.GetValue("category").Split(',');
                foreach (string s in categories)
                {
                    customSubCategory sC = new customSubCategory(node, s.Trim());
                    if (sC.hasFilters && folderToCategoryDict.ContainsKey(sC.category))
                    {
                        foreach(Filter f in sC.filters)
                            f.checks.Add(new Check("folder", folderToCategoryDict[sC.category]));
                    }
                    if (sC.hasFilters && checkForConflicts(sC))
                        subCategories.Add(sC);
                    if (!sC.hasFilters)
                        editList.Add(sC);
                }
            }
            customSCEditDelete(editList);

            foreach (KeyValuePair<string, customSubCategory> kvp in categoryAllSub)
            {
                customSubCategory sC = kvp.Value;
                if (folderToCategoryDict.ContainsKey(kvp.Key))
                {
                    foreach (Filter f in sC.filters)
                        f.checks.Add(new Check("folder", folderToCategoryDict[sC.category]));
                }

                subCategories.Insert(0, sC);
            }

            checkForEmptySubCategories();
            loadIcons();
        }

        /// <summary>
        /// creating subcategories and then trying to edit them during initialisation causes all sorts of problems. Instead, make the edits prior to initialisation
        /// </summary>
        /// <param name="sCs"></param>
        private void customSCEditDelete(List<customSubCategory> sCs)
        {
            foreach (customSubCategory sC in sCs)
            {
                customSubCategory sCToEdit = subCategories.FirstOrDefault(sub => sub.category == sC.category && (sub.subCategoryTitle == sC.oldTitle || sub.subCategoryTitle == sC.subCategoryTitle));

                if (sCToEdit != null)
                {
                    if (!string.IsNullOrEmpty(sC.subCategoryTitle))
                    {
                        sCToEdit.subCategoryTitle = sC.subCategoryTitle;
                        sCToEdit.iconName = sC.iconName;
                    }
                    else
                    {
                        subCategories.Remove(sCToEdit);
                    }
                }
                else
                {
                    subCategories.Add(sC);
                }
            }
        }

        private void associateParts()
        {
            // Build list of mod folder names and Dict associating parts with mods
            List<string> modNames = new List<string>();
            foreach (AvailablePart p in PartLoader.Instance.parts)
            {
                // don't want dummy parts
                if (p.category == PartCategories.none)
                    continue;
                
                if (string.IsNullOrEmpty(p.partUrl))
                    RepairAvailablePartUrl(p);
                
                // if the url is still borked, can't associate a mod to the part
                if (string.IsNullOrEmpty(p.partUrl))
                    continue;
                
                string name = p.partUrl.Split(new char[] { '/', '\\' })[0]; // mod folder name (\\ is escaping the \, read as  '\')
                
                // if we haven't seen any from this mod before
                if (!modNames.Contains(name))
                    modNames.Add(name);

                // associate the mod to the part
                if (!partFolderDict.ContainsKey(p.name))
                    partFolderDict.Add(p.name, name);
                else
                    Log(p.name + " duplicated part key in part-mod dictionary");

                if (p != null && PartType.isEngine(p))
                {
                    foreach (ModuleEngines e in p.partPrefab.GetModuleEngines())
                    {
                        List<string> propellants = new List<string>();
                        foreach (Propellant prop in e.propellants)
                            propellants.Add(prop.name);
                        propellants.Sort();

                        if (!stringListComparer(propellants))
                            propellantCombos.Add(propellants);
                    }
                    foreach (ModuleEnginesFX ex in p.partPrefab.GetModuleEnginesFx())
                    {
                        List<string> propellants = new List<string>();
                        foreach (Propellant prop in ex.propellants)
                            propellants.Add(prop.name);
                        propellants.Sort();

                        if (!stringListComparer(propellants))
                            propellantCombos.Add(propellants);
                    }
                }
            }
            // Create subcategories for Manufacturer category
            foreach (string s in modNames)
            {
                Check ch = new Check("folder", s);
                Filter f = new Filter(false);
                customSubCategory sC = new customSubCategory(s, "Filter by Manufacturer", s);
                
                f.checks.Add(ch);
                sC.filters.Add(f);
                subCategories.Add(sC);
            }
        }

        private bool stringListComparer(List<string> propellants)
        {
            foreach (List<string> ls in propellantCombos)
            {
                if (propellants.Count == ls.Count)
                {
                    List<string> tmp = propellants.Except(ls).ToList();
                    if (!tmp.Any())
                        return true;
                }
            }
            return false;
        }

        internal void editor()
        {
            // set state == 1, we have started processing
            state = 1;

            // clear manufacturers from Filter by Manufacturer
            // Don't rename incase other mods depend on finding it (and the name isn't half bad either...)
            PartCategorizer.Instance.filters.Find(f => f.button.categoryName == "Filter by Manufacturer").subcategories.Clear();

            // Add all the categories
            foreach (customCategory c in Categories)
            {
                c.initialise();
            }

            // icon autoloader pass
            foreach (PartCategorizer.Category c in PartCategorizer.Instance.filters)
            {
                checkIcons(c);
            }

            // create all the new subCategories
            foreach (customSubCategory sC in subCategories)
            {
                try
                {
                    sC.initialise();
                }
                catch (Exception ex)
                {
                    // extended logging for errors
                    Log(sC.subCategoryTitle + " failed to initialise");
                    Log("Category:" + sC.category + ", filter:" + sC.hasFilters + ", Count:" + sC.filters.Count + ", Icon:" + getIcon(sC.iconName) + ", oldTitle:" + sC.oldTitle);
                    Log(ex.StackTrace);
                }
            }

            // update icons
            refreshList();

            // Remove any category with no subCategories (causes major breakages). Removal doesn't actually prevent icon showing, just breakages
            PartCategorizer.Instance.filters.RemoveAll(c => c.subcategories.Count == 0);
            // refresh icons - doesn't work >.<
            // PartCategorizer.Instance.UpdateCategoryNameLabel();

            // reveal categories
            PartCategorizer.Instance.SetAdvancedMode();

            // set state == -1, we have finished processing with no critical errors
            state = -1;
        }

        public void refreshList()
        {
            PartCategorizer.Category Filter = PartCategorizer.Instance.filters.Find(f => f.button.categoryName == "Filter by Function");
            RUIToggleButtonTyped button = Filter.button.activeButton;
            button.SetFalse(button, RUIToggleButtonTyped.ClickType.FORCED);
            button.SetTrue(button, RUIToggleButtonTyped.ClickType.FORCED);
        }

        private bool checkForConflicts(customSubCategory sCToCheck)
        {
            foreach (customSubCategory sC in subCategories) // iterate through the already added sC's
            {
                // collision only possible within a category
                if (sC.category == sCToCheck.category)
                {
                    if (sC.subCategoryTitle == sCToCheck.subCategoryTitle) // if they have the same name, just add the new filters on (OR'd together)
                    {
                        Log(sC.subCategoryTitle + " has multiple entries. Filters are being combined");
                        sCToCheck.filters.AddRange(sC.filters);
                        return false; // all other elements of this list have already been check for this condition. Don't need to continue
                    }
                    if (compareFilterLists(sC.filters, sCToCheck.filters)) // check for duplicated filters
                    {
                        Log(sC.subCategoryTitle + " has duplicated the filters of " + sCToCheck.subCategoryTitle);
                        return false; // ignore this subCategory, only the first processed sC in a conflict will get through
                    }
                }
            }
            return true;
        }

        private bool compareFilterLists(List<Filter> fLA, List<Filter> fLB)
        {
            if (fLA.Count == 0 || fLB.Count == 0)
                return false;

            if (fLA.Count != fLB.Count)
                return false;

            foreach (Filter fA in fLA)
            {
                if (!fLB.Any(fB => fB.Equals(fA)))
                    return false;
            }
            return true;
        }

        private void checkIcons(PartCategorizer.Category category)
        {
            foreach (PartCategorizer.Category c in category.subcategories)
            {
                // if any of the names of the loaded icons match the subCategory name, then replace their current icon with the match
                if (iconDict.ContainsKey(c.button.categoryName))
                    c.button.SetIcon(getIcon(c.button.categoryName));
            }
        }

        private static void loadIcons()
        {
            List<GameDatabase.TextureInfo> texList = GameDatabase.Instance.databaseTexture.Where(t => t.texture != null 
                                                                                                && t.texture.height <= 40 && t.texture.width <= 40
                                                                                                && t.texture.width >= 25 && t.texture.height >= 25
                                                                                                ).ToList();

            Dictionary<string, GameDatabase.TextureInfo> texDict = new Dictionary<string, GameDatabase.TextureInfo>();
            // using a dictionary for looking up _selected textures. Else the list has to be iterated over for every texture
            foreach(GameDatabase.TextureInfo t in texList)
            {
                if (!texDict.ContainsKey(t.name))
                    texDict.Add(t.name, t);
                else
                {
                    int i = 1;
                    while (texDict.ContainsKey(t.name + i.ToString()) && i < 1000)
                        i++;
                    if (i != 1000)
                    {
                        texDict.Add(t.name + i.ToString(), t);
                        Log(t.name+i.ToString());
                    }
                }
            }

            foreach (GameDatabase.TextureInfo t in texList)
            {
                Texture2D selectedTex = null;

                if (texDict.ContainsKey(t.name + "_selected"))
                    selectedTex = texDict[t.name + "_selected"].texture;
                else
                    selectedTex = t.texture;

                string name = t.name.Split(new char[] { '/', '\\' }).Last();
                if (iconDict.ContainsKey(name))
                {
                    int i = 1;
                    while (iconDict.ContainsKey(name + i.ToString()) && i < 1000)
                        i++;
                    if (i != 1000)
                        name = name + i.ToString();
                    Log("Duplicated texture name by texture " + t.name + ". New reference is: " + name);
                }

                PartCategorizer.Icon icon = new PartCategorizer.Icon(name, t.texture, selectedTex, false);
                
                // shouldn't be neccesary to check, but just in case...
                if (!iconDict.ContainsKey(icon.name))
                    iconDict.Add(icon.name, icon);
            }
        }

        public static PartCategorizer.Icon getIcon(string name)
        {
            if (iconDict.ContainsKey(name))
            {
                return iconDict[name];
            }
            else if (PartCategorizer.Instance.iconDictionary.ContainsKey(name))
            {
                return PartCategorizer.Instance.iconDictionary[name];
            }
            else if (name.StartsWith("stock_"))
            {
                PartCategorizer.Category fbf = PartCategorizer.Instance.filters.Find(c => c.button.categoryName == "Filter by Function");
                name = name.Substring(6);
                return fbf.subcategories.FirstOrDefault(sC => sC.button.categoryName == name).button.icon;
            }
            return null;
        }

        // credit to EvilReeperx for this lifesaving function
        private void RepairAvailablePartUrl(AvailablePart ap)
        {
            var url = GameDatabase.Instance.GetConfigs("PART").FirstOrDefault(u => u.name.Replace('_', '.') == ap.name);

            if (url == null)
                return;

            ap.partUrl = url.url;
        }

        private void checkForEmptySubCategories()
        {
            List<customSubCategory> notEmpty = new List<customSubCategory>();

            foreach (customSubCategory sC in subCategories)
            {
                if (!sC.hasFilters)
                {
                    notEmpty.Add(sC);
                    continue;
                }
                foreach (AvailablePart p in PartLoader.Instance.parts)
                {
                    if (sC.checkFilters(p))
                    {
                        notEmpty.Add(sC);
                        break;
                    }
                }
            }
            subCategories = notEmpty;
        }

        internal static void Log(object o)
        {
            Debug.Log("[Filter Extensions] " + o);
        }
    }
}
