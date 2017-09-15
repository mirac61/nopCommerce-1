﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Newtonsoft.Json;
using Nop.Core.ComponentModel;
using Nop.Core.Configuration;

//Contributor: Umbraco (http://www.umbraco.com). Thanks a lot! 
//SEE THIS POST for full details of what this does - http://shazwazza.com/post/Developing-a-plugin-framework-in-ASPNET-with-medium-trust.aspx

namespace Nop.Core.Plugins
{
    /// <summary>
    /// Sets the application up for the plugin referencing
    /// </summary>
    public class PluginManager
    {
        #region Const

        private const string ObsoleteInstalledPluginsFilePath = "~/App_Data/InstalledPlugins.txt";
        private const string InstalledPluginsFilePath_ = "~/App_Data/installedPlugins.json";
        private const string PluginsPath = "~/Plugins";
        private const string PluginsPathName = "Plugins";
        private const string ShadowCopyPath = "~/Plugins/bin";
        private const string RefsPathName = "refs";
        private const string PluginDescriptionFileName = "plugin.json";

        #endregion

        #region Fields

        private static readonly ReaderWriterLockSlim Locker = new ReaderWriterLockSlim();
        private static DirectoryInfo _shadowCopyFolder;
        private static readonly List<string> BaseAppLibraries;

        #endregion

        #region Ctor

        static PluginManager()
        {
            //get all libraries from /bin/{version}/ directory
            BaseAppLibraries = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory)
                .GetFiles("*.dll", SearchOption.TopDirectoryOnly).Select(fi => fi.Name).ToList();

            //get all libraries from base site directory
            if(!AppDomain.CurrentDomain.BaseDirectory.Equals(Environment.CurrentDirectory, StringComparison.InvariantCultureIgnoreCase))
                BaseAppLibraries.AddRange(new DirectoryInfo(Environment.CurrentDirectory).GetFiles("*.dll", SearchOption.TopDirectoryOnly).Select(fi => fi.Name));

            //get all libraries from refs directory
            var refsPathName = new DirectoryInfo(Path.Combine(Environment.CurrentDirectory, RefsPathName));
            if(refsPathName.Exists)
                BaseAppLibraries.AddRange(refsPathName.GetFiles("*.dll", SearchOption.TopDirectoryOnly).Select(fi => fi.Name));
        }

        #endregion

        #region Properties

        /// <summary>
        /// Gets the path to file that contains installed plugin system names
        /// </summary>
        public static string InstalledPluginsFilePath => InstalledPluginsFilePath_;

        /// <summary>
        /// Returns a collection of all referenced plugin assemblies that have been shadow copied
        /// </summary>
        public static IEnumerable<PluginDescriptor> ReferencedPlugins { get; set; }

        /// <summary>
        /// Returns a collection of all plugin which are not compatible with the current version
        /// </summary>
        public static IEnumerable<string> IncompatiblePlugins { get; set; }

        #endregion

        #region Methods

        /// <summary>
        /// Initialize
        /// </summary>
        /// <param name="applicationPartManager">Application part manager</param>
        /// <param name="config">Config</param>
        public static void Initialize(ApplicationPartManager applicationPartManager, NopConfig config)
        {
            if (applicationPartManager == null)
                throw new ArgumentNullException(nameof(applicationPartManager));

            if (config == null)
                throw new ArgumentNullException(nameof(config));


            using (new WriteLockDisposable(Locker))
            {
                // TODO: Add verbose exception handling / raising here since this is happening on app startup and could
                // prevent app from starting altogether
                var pluginFolder = new DirectoryInfo(CommonHelper.MapPath(PluginsPath));
                _shadowCopyFolder = new DirectoryInfo(CommonHelper.MapPath(ShadowCopyPath));

                var referencedPlugins = new List<PluginDescriptor>();
                var incompatiblePlugins = new List<string>();
                
                try
                {
                    var installedPluginSystemNames = GetInstalledPluginNames(CommonHelper.MapPath(InstalledPluginsFilePath));

                    Debug.WriteLine("Creating shadow copy folder and querying for dlls");
                    //ensure folders are created
                    Directory.CreateDirectory(pluginFolder.FullName);
                    Directory.CreateDirectory(_shadowCopyFolder.FullName);
                    
                    //get list of all files in bin
                    var binFiles = _shadowCopyFolder.GetFiles("*", SearchOption.AllDirectories);
                    if (config.ClearPluginShadowDirectoryOnStartup)
                    {
                        //clear out shadow copied plugins
                        foreach (var f in binFiles)
                        {
                            Debug.WriteLine("Deleting " + f.Name);
                            try
                            {
                                //ignore index.htm
                                var fileName = Path.GetFileName(f.FullName);
                                if (fileName.Equals("index.htm", StringComparison.InvariantCultureIgnoreCase))
                                    continue;

                                File.Delete(f.FullName);
                            }
                            catch (Exception exc)
                            {
                                Debug.WriteLine("Error deleting file " + f.Name + ". Exception: " + exc);
                            }
                        }
                    }

                   
                    //load description files
                    foreach (var dfd in GetDescriptionFilesAndDescriptors(pluginFolder))
                    {
                        var descriptionFile = dfd.Key;
                        var pluginDescriptor = dfd.Value;

                        //ensure that version of plugin is valid
                        if (!pluginDescriptor.SupportedVersions.Contains(NopVersion.CurrentVersion, StringComparer.InvariantCultureIgnoreCase))
                        {
                            incompatiblePlugins.Add(pluginDescriptor.SystemName);
                            continue;
                        }

                        //some validation
                        if (String.IsNullOrWhiteSpace(pluginDescriptor.SystemName))
                            throw new Exception($"A plugin '{descriptionFile.FullName}' has no system name. Try assigning the plugin a unique name and recompiling.");
                        if (referencedPlugins.Contains(pluginDescriptor))
                            throw new Exception($"A plugin with '{pluginDescriptor.SystemName}' system name is already defined");

                        //set 'Installed' property
                        pluginDescriptor.Installed = installedPluginSystemNames
                            .FirstOrDefault(x => x.Equals(pluginDescriptor.SystemName, StringComparison.InvariantCultureIgnoreCase)) != null;

                        try
                        {
                            if (descriptionFile.Directory == null)
                                throw new Exception($"Directory cannot be resolved for '{descriptionFile.Name}' description file");
                            //get list of all DLLs in plugins (not in bin!)
                            var pluginFiles = descriptionFile.Directory.GetFiles("*.dll", SearchOption.AllDirectories)
                                //just make sure we're not registering shadow copied plugins
                                .Where(x => !binFiles.Select(q => q.FullName).Contains(x.FullName))
                                .Where(x => IsPackagePluginFolder(x.Directory))
                                .ToList();

                            //other plugin description info
                            var mainPluginFile = pluginFiles
                                .FirstOrDefault(x => x.Name.Equals(pluginDescriptor.AssemblyFileName, StringComparison.InvariantCultureIgnoreCase));
                            pluginDescriptor.OriginalAssemblyFile = mainPluginFile;

                            //shadow copy main plugin file
                            pluginDescriptor.ReferencedAssembly = PerformFileDeploy(mainPluginFile, applicationPartManager);

                            //load all other referenced assemblies now
                            foreach (var plugin in pluginFiles
                                .Where(x => !x.Name.Equals(mainPluginFile.Name, StringComparison.InvariantCultureIgnoreCase))
                                .Where(x => !IsAlreadyLoaded(x)))
                                    PerformFileDeploy(plugin, applicationPartManager);
                            
                            //init plugin type (only one plugin per assembly is allowed)
                            foreach (var t in pluginDescriptor.ReferencedAssembly.GetTypes())
                                if (typeof(IPlugin).IsAssignableFrom(t))
                                    if (!t.IsInterface)
                                        if (t.IsClass && !t.IsAbstract)
                                        {
                                            pluginDescriptor.PluginType = t;
                                            break;
                                        }

                            referencedPlugins.Add(pluginDescriptor);
                        }
                        catch (ReflectionTypeLoadException ex)
                        {
                            //add a plugin name. this way we can easily identify a problematic plugin
                            var msg = $"Plugin '{pluginDescriptor.FriendlyName}'. ";
                            foreach (var e in ex.LoaderExceptions)
                                msg += e.Message + Environment.NewLine;

                            var fail = new Exception(msg, ex);
                            throw fail;
                        }
                        catch (Exception ex)
                        {
                            //add a plugin name. this way we can easily identify a problematic plugin
                            var msg = $"Plugin '{pluginDescriptor.FriendlyName}'. {ex.Message}";

                            var fail = new Exception(msg, ex);
                            throw fail;
                        }
                    }
                }
                catch (Exception ex)
                {
                    var msg = string.Empty;
                    for (var e = ex; e != null; e = e.InnerException)
                        msg += e.Message + Environment.NewLine;

                    var fail = new Exception(msg, ex);
                    throw fail;
                }


                ReferencedPlugins = referencedPlugins;
                IncompatiblePlugins = incompatiblePlugins;

            }
        }

        /// <summary>
        /// Mark plugin as installed
        /// </summary>
        /// <param name="systemName">Plugin system name</param>
        public static void MarkPluginAsInstalled(string systemName)
        {
            if (string.IsNullOrEmpty(systemName))
                throw new ArgumentNullException(nameof(systemName));

            var filePath = CommonHelper.MapPath(InstalledPluginsFilePath);

            //create file if not exists
            if (!File.Exists(filePath))
            {
                //we use 'using' to close the file after it's created
                using (File.Create(filePath)) { }
            }

            //get installed plugin names
            var installedPluginSystemNames = GetInstalledPluginNames(filePath);

            //add plugin system name to the list if doesn't already exist
            var alreadyMarkedAsInstalled = installedPluginSystemNames.Any(pluginName => pluginName.Equals(systemName, StringComparison.InvariantCultureIgnoreCase));
            if (!alreadyMarkedAsInstalled)
                installedPluginSystemNames.Add(systemName);

            //save installed plugin names to the file
            SaveInstalledPluginNames(installedPluginSystemNames,filePath);
        }

        /// <summary>
        /// Mark plugin as uninstalled
        /// </summary>
        /// <param name="systemName">Plugin system name</param>
        public static void MarkPluginAsUninstalled(string systemName)
        {
            if (string.IsNullOrEmpty(systemName))
                throw new ArgumentNullException(nameof(systemName));

            var filePath = CommonHelper.MapPath(InstalledPluginsFilePath);

            //create file if not exists
            if (!File.Exists(filePath))
            {
                //we use 'using' to close the file after it's created
                using (File.Create(filePath)) { }
            }

            //get installed plugin names
            var installedPluginSystemNames = GetInstalledPluginNames(filePath);

            //remove plugin system name from the list if exists
            var alreadyMarkedAsInstalled = installedPluginSystemNames.Any(pluginName => pluginName.Equals(systemName, StringComparison.InvariantCultureIgnoreCase));
            if (alreadyMarkedAsInstalled)
                installedPluginSystemNames.Remove(systemName);

            //save installed plugin names to the file
            SaveInstalledPluginNames(installedPluginSystemNames,filePath);
        }

        /// <summary>
        /// Mark plugin as uninstalled
        /// </summary>
        public static void MarkAllPluginsAsUninstalled()
        {
            var filePath = CommonHelper.MapPath(InstalledPluginsFilePath);
            if (File.Exists(filePath))
                File.Delete(filePath);
        }

        /// <summary>
        /// Find a plugin descriptor by some type which is located into the same assembly as plugin
        /// </summary>
        /// <param name="typeInAssembly">Type</param>
        /// <returns>Plugin descriptor if exists; otherwise null</returns>
        public static PluginDescriptor FindPlugin(Type typeInAssembly)
        {
            if (typeInAssembly == null)
                throw new ArgumentNullException(nameof(typeInAssembly));

            if (ReferencedPlugins == null)
                return null;

            return ReferencedPlugins.FirstOrDefault(plugin => plugin.ReferencedAssembly != null
                && plugin.ReferencedAssembly.FullName.Equals(typeInAssembly.Assembly.FullName, StringComparison.InvariantCultureIgnoreCase));
        }

        /// <summary>
        /// Save plugin descriptor to the plugin description file
        /// </summary>
        /// <param name="pluginDescriptor">Plugin descriptor</param>
        public static void SavePluginDescriptor(PluginDescriptor pluginDescriptor)
        {
            if (pluginDescriptor == null)
                throw new ArgumentException(nameof(pluginDescriptor));

            //get the description file path
            if (pluginDescriptor.OriginalAssemblyFile == null)
                throw new Exception($"Cannot load original assembly path for {pluginDescriptor.SystemName} plugin.");

            var filePath = Path.Combine(pluginDescriptor.OriginalAssemblyFile.Directory.FullName, PluginDescriptionFileName);
            if (!File.Exists(filePath))
                throw new Exception($"Description file for {pluginDescriptor.SystemName} plugin does not exist. {filePath}");

            //save the file
            var text = JsonConvert.SerializeObject(pluginDescriptor, Formatting.Indented);
            File.WriteAllText(filePath, text);
        }

        #endregion

        #region Utilities

        /// <summary>
        /// Get description files
        /// </summary>
        /// <param name="pluginFolder">Plugin directory info</param>
        /// <returns>Original and parsed description files</returns>
        private static IEnumerable<KeyValuePair<FileInfo, PluginDescriptor>> GetDescriptionFilesAndDescriptors(DirectoryInfo pluginFolder)
        {
            if (pluginFolder == null)
                throw new ArgumentNullException(nameof(pluginFolder));

            //create list (<file info, parsed plugin descritor>)
            var result = new List<KeyValuePair<FileInfo, PluginDescriptor>>();
            
            //add display order and path to list
            foreach (var descriptionFile in pluginFolder.GetFiles(PluginDescriptionFileName, SearchOption.AllDirectories))
            {
                if (!IsPackagePluginFolder(descriptionFile.Directory))
                    continue;

                //parse file
                var pluginDescriptor = GetPluginDescriptor(descriptionFile.FullName);

                //populate list
                result.Add(new KeyValuePair<FileInfo, PluginDescriptor>(descriptionFile, pluginDescriptor));
            }

            //sort list by display order. NOTE: Lowest DisplayOrder will be first i.e 0 , 1, 1, 1, 5, 10
            //it's required: https://www.nopcommerce.com/boards/t/17455/load-plugins-based-on-their-displayorder-on-startup.aspx
            result.Sort((firstPair, nextPair) => firstPair.Value.DisplayOrder.CompareTo(nextPair.Value.DisplayOrder));
            return result;
        }

        /// <summary>
        /// Get plugin descriptor from the plugin description file
        /// </summary>
        /// <param name="filePath">Path to the description file</param>
        /// <returns>Plugin descriptor</returns>
        private static PluginDescriptor GetPluginDescriptor(string filePath)
        {
            var text = File.ReadAllText(filePath);
            if (string.IsNullOrEmpty(text))
                return new PluginDescriptor();

            //get plugin descriptor from the JSON file
            var descriptor = JsonConvert.DeserializeObject<PluginDescriptor>(text);

            //nopCommerce 2.00 didn't have 'SupportedVersions' parameter, so let's set it to "2.00"
            if (!descriptor.SupportedVersions.Any())
                descriptor.SupportedVersions.Add("2.00");

            return descriptor;
        }

        /// <summary>
        /// Get system names of installed plugins
        /// </summary>
        /// <param name="filePath">Path to the file</param>
        /// <returns>List of plugin system names</returns>
        private static IList<string> GetInstalledPluginNames(string filePath)
        {
            //check whether file exists
            if (!File.Exists(filePath))
            {
                //if not, try to parse the file that was used in previous nopCommerce versions
                filePath = CommonHelper.MapPath(ObsoleteInstalledPluginsFilePath);
                if (!File.Exists(filePath))
                    return new List<string>();

                //get plugin system names from the old txt file
                var pluginSystemNames = new List<string>();
                using (var reader = new StringReader(File.ReadAllText(filePath)))
                {
                    var pluginName = string.Empty;
                    while ((pluginName = reader.ReadLine()) != null)
                    {
                        if (!string.IsNullOrWhiteSpace(pluginName))
                            pluginSystemNames.Add(pluginName.Trim());
                    }
                }

                //save system names of installed plugins to the new file
                SaveInstalledPluginNames(pluginSystemNames, CommonHelper.MapPath(InstalledPluginsFilePath));

                //and delete the old one
                File.Delete(filePath);

                return pluginSystemNames;
            }

            var text = File.ReadAllText(filePath);
            if (string.IsNullOrEmpty(text))
                return new List<string>();

            //get plugin system names from the JSON file
            return JsonConvert.DeserializeObject<IList<string>>(text);
        }

        /// <summary>
        /// Save system names of installed plugins to the file
        /// </summary>
        /// <param name="pluginSystemNames">List of plugin system names</param>
        /// <param name="filePath">Path to the file</param>
        private static void SaveInstalledPluginNames(IList<string> pluginSystemNames, string filePath)
        {
            //save the file
            var text = JsonConvert.SerializeObject(pluginSystemNames, Formatting.Indented);
            File.WriteAllText(filePath, text);
        }

        /// <summary>
        /// Indicates whether assembly file is already loaded
        /// </summary>
        /// <param name="fileInfo">File info</param>
        /// <returns>Result</returns>
        private static bool IsAlreadyLoaded(FileInfo fileInfo)
        {
            //search library file name in base directory to ignore already existing (loaded) libraries
            //(we do it because not all libraries are loaded immediately after application start)
            if (BaseAppLibraries.Any(sli => sli.Equals(fileInfo.Name, StringComparison.InvariantCultureIgnoreCase)))
                return true;

            //compare full assembly name
            //var fileAssemblyName = AssemblyName.GetAssemblyName(fileInfo.FullName);
            //foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            //{
            //    if (a.FullName.Equals(fileAssemblyName.FullName, StringComparison.InvariantCultureIgnoreCase))
            //        return true;
            //}
            //return false;

            //do not compare the full assembly name, just filename
            try
            {
                var fileNameWithoutExt = Path.GetFileNameWithoutExtension(fileInfo.FullName);
                if (string.IsNullOrEmpty(fileNameWithoutExt))
                    throw new Exception($"Cannot get file extension for {fileInfo.Name}");

                foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                {
                    var assemblyName = a.FullName.Split(',').FirstOrDefault();
                    if (fileNameWithoutExt.Equals(assemblyName, StringComparison.InvariantCultureIgnoreCase))
                        return true;
                }
            }
            catch (Exception exc)
            {
                Debug.WriteLine("Cannot validate whether an assembly is already loaded. " + exc);
            }
            return false;
        }

        /// <summary>
        /// Perform file deploy
        /// </summary>
        /// <param name="plug">Plugin file info</param>
        /// <param name="applicationPartManager">Application part manager</param>
        /// <returns>Assembly</returns>
        private static Assembly PerformFileDeploy(FileInfo plug, ApplicationPartManager applicationPartManager)
        {
            if (plug.Directory == null || plug.Directory.Parent == null)
                throw new InvalidOperationException("The plugin directory for the " + plug.Name + " file exists in a folder outside of the allowed nopCommerce folder hierarchy");

            //but in order to avoid possible issues we still copy libraries into ~/Plugins/bin/ directory
            var shadowCopyPlugFolder = Directory.CreateDirectory(_shadowCopyFolder.FullName);
            var shadowCopiedPlug = ShadowCopyFile(plug, shadowCopyPlugFolder);

            //we can now register the plugin definition
            var shadowCopiedAssembly = Assembly.Load(AssemblyName.GetAssemblyName(shadowCopiedPlug.FullName));
            Debug.WriteLine("Adding to ApplicationParts: '{0}'", shadowCopiedAssembly.FullName);
            applicationPartManager.ApplicationParts.Add(new AssemblyPart(shadowCopiedAssembly));

            return shadowCopiedAssembly;
        }
        
        /// <summary>
        /// Copy the plugin file to shadow copy directory
        /// </summary>
        /// <param name="plug"></param>
        /// <param name="shadowCopyPlugFolder"></param>
        /// <returns></returns>
        private static FileInfo ShadowCopyFile(FileInfo plug, DirectoryInfo shadowCopyPlugFolder)
        {
            var shouldCopy = true;
            var shadowCopiedPlug = new FileInfo(Path.Combine(shadowCopyPlugFolder.FullName, plug.Name));

            //check if a shadow copied file already exists and if it does, check if it's updated, if not don't copy
            if (shadowCopiedPlug.Exists)
            {
                //it's better to use LastWriteTimeUTC, but not all file systems have this property
                //maybe it is better to compare file hash?
                var areFilesIdentical = shadowCopiedPlug.CreationTimeUtc.Ticks >= plug.CreationTimeUtc.Ticks;
                if (areFilesIdentical)
                {
                    Debug.WriteLine("Not copying; files appear identical: '{0}'", shadowCopiedPlug.Name);
                    shouldCopy = false;
                }
                else
                {
                    //delete an existing file

                    //More info: https://www.nopcommerce.com/boards/t/11511/access-error-nopplugindiscountrulesbillingcountrydll.aspx?p=4#60838
                    Debug.WriteLine("New plugin found; Deleting the old file: '{0}'", shadowCopiedPlug.Name);
                    File.Delete(shadowCopiedPlug.FullName);
                }
            }

            if (!shouldCopy)
                return shadowCopiedPlug;

            try
            {
                File.Copy(plug.FullName, shadowCopiedPlug.FullName, true);
            }
            catch (IOException)
            {
                Debug.WriteLine(shadowCopiedPlug.FullName + " is locked, attempting to rename");
                //this occurs when the files are locked,
                //for some reason devenv locks plugin files some times and for another crazy reason you are allowed to rename them
                //which releases the lock, so that it what we are doing here, once it's renamed, we can re-shadow copy
                try
                {
                    var oldFile = shadowCopiedPlug.FullName + Guid.NewGuid().ToString("N") + ".old";
                    File.Move(shadowCopiedPlug.FullName, oldFile);
                }
                catch (IOException exc)
                {
                    throw new IOException(shadowCopiedPlug.FullName + " rename failed, cannot initialize plugin", exc);
                }
                //ok, we've made it this far, now retry the shadow copy
                File.Copy(plug.FullName, shadowCopiedPlug.FullName, true);
            }

            return shadowCopiedPlug;
        }

        /// <summary>
        /// Determines if the folder is a bin plugin folder for a package
        /// </summary>
        /// <param name="folder"></param>
        /// <returns></returns>
        private static bool IsPackagePluginFolder(DirectoryInfo folder)
        {
            if (folder == null) return false;
            if (folder.Parent == null) return false;
            if (!folder.Parent.Name.Equals(PluginsPathName, StringComparison.InvariantCultureIgnoreCase)) return false;

            return true;
        }

        #endregion
    }
}
