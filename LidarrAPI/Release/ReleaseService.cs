﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using LidarrAPI.Database;
using LidarrAPI.Release.AppVeyor;
using LidarrAPI.Release.Github;
using LidarrAPI.Update;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NLog;

namespace LidarrAPI.Release
{
    public class ReleaseService
    {
        private static readonly ConcurrentDictionary<Branch, SemaphoreSlim> ReleaseLocks;

        private readonly IServiceProvider _serviceProvider;
        
        private readonly ConcurrentDictionary<Branch, Type> _releaseBranches;

        private readonly Config _config;

        static ReleaseService()
        {
            ReleaseLocks = new ConcurrentDictionary<Branch, SemaphoreSlim>();
            ReleaseLocks.TryAdd(Branch.Develop, new SemaphoreSlim(1, 1));
            ReleaseLocks.TryAdd(Branch.Nightly, new SemaphoreSlim(1, 1));
        }

        public ReleaseService(IServiceProvider serviceProvider, IOptions<Config> configOptions)
        {
            _serviceProvider = serviceProvider;

            _releaseBranches = new ConcurrentDictionary<Branch, Type>();
            _releaseBranches.TryAdd(Branch.Develop, typeof(GithubReleaseSource));
            _releaseBranches.TryAdd(Branch.Nightly, typeof(AppVeyorReleaseSource));

            _config = configOptions.Value;
        }

        public async Task UpdateReleasesAsync(Branch branch)
        {
            if (!_releaseBranches.TryGetValue(branch, out var releaseSourceBaseType))
            {
                throw new NotImplementedException($"{branch} does not have a release source.");
            }

            if (!ReleaseLocks.TryGetValue(branch, out var releaseLock))
            {
                throw new NotImplementedException($"{branch} does not have a release lock.");
            }

            var obtainedLock = false;

            try
            {
                obtainedLock = await releaseLock.WaitAsync(TimeSpan.FromMinutes(5));

                if (obtainedLock)
                {
                    var releaseSourceInstance = (ReleaseSourceBase) _serviceProvider.GetRequiredService(releaseSourceBaseType);

                    releaseSourceInstance.ReleaseBranch = branch;

                    var hasNewRelease = await releaseSourceInstance.StartFetchReleasesAsync();
                    if (hasNewRelease)
                    {
                        await CallTriggers(branch);
                    }
                }
            }
            finally
            {
                if (obtainedLock)
                {
                    releaseLock.Release();
                }
            }
        }

        private async Task CallTriggers(Branch branch)
        {
            var logger = LogManager.GetCurrentClassLogger();
            logger.Debug($"Calling triggers for {branch}");

            List<string> triggers;
            if (!_config.Triggers.TryGetValue(branch, out triggers) || triggers.Count == 0)
            {
                logger.Debug($"No triggers for {branch}");
                return;
            }

            foreach (var trigger in triggers)
            {
                try
                {
                    logger.Debug($"Triggering {trigger}");
                    var request = WebRequest.CreateHttp(trigger);
                    request.Method = "GET";
                    request.UserAgent = "LidarrAPI.Update/Trigger";
                    request.KeepAlive = false;
                    request.Timeout = 2500;
                    request.ReadWriteTimeout = 2500;
                    request.ContinueTimeout = 2500;

                    var response = await request.GetResponseAsync();
                    response.Dispose();
                }
                catch (Exception)
                {
                    // don't care.
                }
            }
        }
    }
}