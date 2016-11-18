﻿using System;
using System.IO;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Smidge.CompositeFiles;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.Extensions.FileProviders;
using System.Linq;
//using Microsoft.AspNetCore.NodeServices;
using Smidge.Models;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.PlatformAbstractions;
using Smidge.Options;
using Smidge.FileProcessors;
using Smidge.Hashing;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.AspNetCore.Http.Extensions;
using Smidge.Cache;

[assembly: InternalsVisibleTo("Smidge.Tests")]

namespace Smidge
{
    public static class SmidgeStartup
    {


        public static IServiceCollection AddSmidge(this IServiceCollection services, 
            IConfiguration smidgeConfiguration = null, 
            IFileProvider fileProvider = null)
        {            
            services.TryAddSingleton<IHttpContextAccessor, HttpContextAccessor>();
            services.TryAddSingleton<IActionContextAccessor, ActionContextAccessor>();
            
            services.AddTransient<IConfigureOptions<SmidgeOptions>, SmidgeOptionsSetup>();

            services.AddSingleton<IRequestHelper, RequestHelper>();            
            services.AddSingleton<IWebsiteInfo, AutoWebsiteInfo>();
            services.AddSingleton<IBundleFileSetGenerator, BundleFileSetGenerator>();
            services.AddSingleton<IHasher, Crc32Hasher>();
            services.AddSingleton<IBundleManager, BundleManager>();
            services.AddSingleton<PreProcessPipelineFactory>();
            services.AddSingleton<FileSystemHelper>(p =>
            {
                var hosting = p.GetRequiredService<IHostingEnvironment>();
                var provider = fileProvider ?? hosting.WebRootFileProvider;
                return new FileSystemHelper(hosting, p.GetRequiredService<ISmidgeConfig>(), provider, p.GetRequiredService<IHasher>());
            });
            services.AddSingleton<PreProcessManager>();
            services.AddSingleton<ISmidgeConfig>((p) =>
            {
                if (smidgeConfiguration == null)
                {
                    return new SmidgeConfig(p.GetRequiredService<IHostingEnvironment>());
                }
                return new SmidgeConfig(smidgeConfiguration);
            });
            
            services.AddSingleton<ICacheBuster, ConfigCacheBuster>();
            services.AddSingleton<ICacheBuster, AppDomainLifetimeCacheBuster>();
            services.AddSingleton<CacheBusterResolver>();

            services.AddScoped<DynamicallyRegisteredWebFiles>();
            services.AddScoped<SmidgeHelper>();
            services.AddScoped<IUrlManager, DefaultUrlManager>();

            //pre-processors
            services.AddSingleton<IPreProcessor, JsMinifier>();
            services.AddSingleton<IPreProcessor, CssMinifier>();            
            services.AddSingleton<IPreProcessor, CssImportProcessor>();
            services.AddSingleton<IPreProcessor, CssUrlProcessor>();
            
            //conventions
            services.AddSingleton<FileProcessingConventions>();
            services.AddSingleton<IFileProcessingConvention, MinifiedFilePathConvention>();

            //Add the controller models as DI services - these get auto created for model binding
            services.AddTransient<BundleRequestModel>();
            services.AddTransient<CompositeFileModel>();

            return services;
        }
        
        public static void UseSmidge(this IApplicationBuilder app, Action<IBundleManager> configureBundles = null)
        {
            //Create custom route
            app.UseMvc(routes =>
            {
                routes.MapRoute(
                    "SmidgeComposite",
                    "sc/{file}",
                    new { controller = "Smidge", action = "Composite" });

                routes.MapRoute(
                    "SmidgeBundle",
                    "sb/{bundle}",
                    new { controller = "Smidge", action = "Bundle" });
            });

            if (configureBundles != null)
            {
                var bundleManager = app.ApplicationServices.GetRequiredService<IBundleManager>();
                configureBundles(bundleManager);

                //TODO: Now that they are configured we need to wire up the file watching event handlers
                // to the bundle manager, currently these are on the Bundle, but that is not good enough
                // since we need the bundle name
                foreach (var webFileType in new[] {WebFileType.Css, WebFileType.Js })
                {
                    var names = bundleManager.GetBundleNames(webFileType);
                    foreach (var cssName in names)
                    {
                        var bundle = bundleManager.GetBundle(cssName);
                        WireUpFileWatchEventHandlers(cssName, bundle);
                    }
                }
            }    

        }

        private static void WireUpFileWatchEventHandlers(string bundleName, Bundle bundle)
        {
            if (bundle.BundleOptions == null) return;

            if (bundle.BundleOptions.DebugOptions.FileWatchOptions.Enabled)
            {
                bundle.BundleOptions.DebugOptions.FileWatchOptions.FileModified += FileWatchOptions_FileModified(bundleName, bundle);
            }
            if (bundle.BundleOptions.ProductionOptions.FileWatchOptions.Enabled)
            {
                bundle.BundleOptions.ProductionOptions.FileWatchOptions.FileModified += FileWatchOptions_FileModified(bundleName, bundle);
            }
        }

        private static EventHandler<FileWatchEventArgs> FileWatchOptions_FileModified(string bundleName, Bundle bundle)
        {
            return (sender, args) =>
            {
                FileWatchOptions_FileModified(bundleName, bundle, args);
            };
        }

        private static void FileWatchOptions_FileModified(string bundleName, Bundle bundle, FileWatchEventArgs e)
        {
            //this file is part of this bundle, so the persisted processed/combined/compressed  will need to be 
            // invalidated/deleted/renamed
            foreach (var compressionType in new[] { CompressionType.deflate, CompressionType.gzip, CompressionType.none })
            {
                var compFilePath = e.FileSystemHelper.GetCurrentCompositeFilePath(compressionType, bundleName);
                if (File.Exists(compFilePath))
                {
                    File.Delete(compFilePath);
                }
            }
        }

        //private static System.Threading.ReaderWriterLockSlim _locker = new System.Threading.ReaderWriterLockSlim();
    }
}
