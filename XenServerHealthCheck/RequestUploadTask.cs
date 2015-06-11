﻿/* Copyright (c) Citrix Systems Inc. 
 * All rights reserved. 
 * 
 * Redistribution and use in source and binary forms, 
 * with or without modification, are permitted provided 
 * that the following conditions are met: 
 * 
 * *   Redistributions of source code must retain the above 
 *     copyright notice, this list of conditions and the 
 *     following disclaimer. 
 * *   Redistributions in binary form must reproduce the above 
 *     copyright notice, this list of conditions and the 
 *     following disclaimer in the documentation and/or other 
 *     materials provided with the distribution. 
 * 
 * THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND 
 * CONTRIBUTORS "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, 
 * INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES OF 
 * MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE 
 * DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR 
 * CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, 
 * SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, 
 * BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR 
 * SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS 
 * INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, 
 * WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING 
 * NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE 
 * OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF 
 * SUCH DAMAGE.
 */
using System;
using System.Collections.Generic;
using XenAdmin.Network;
using XenAPI;

namespace XenServerHealthCheck
{
    public class RequestUploadTask
    {
        private static readonly log4net.ILog log = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        private static T Get<T>(Dictionary<string, T> d, string k) where T : class
        {
            return d != null && d.ContainsKey(k) ? d[k] : null;
        }

        private static int IntKey(Dictionary<string, string> d, string k, int def)
        {
            int result;
            string s = Get(d, k);
            return s != null && int.TryParse(s, out result) ? result : def;
        }

        private static bool BoolKey(Dictionary<string, string> d, string k)
        {
            string v = Get(d, k);
            return v == null ? false : v == "true";
        }

        private static double DueAfterHour = 24;
        private static bool CanLock(string UploadLock)
        {
            if (UploadLock.Length == 0)
                return true;

            List<string> currentLock = new List<string>(UploadLock.Split('|'));
            DateTime currentTime = DateTime.UtcNow;
            DateTime lockTime = DateTime.Parse(currentLock[1]);

            if (currentLock[0] == System.Configuration.ConfigurationManager.AppSettings["UUID"])
            {
                if ((DateTime.Compare(lockTime.AddHours(DueAfterHour), currentTime) <= 0))
                    return true;
            }
            else if ((DateTime.Compare(lockTime.AddHours(DueAfterHour), currentTime) <= 0))
                return true;
            return false;
        }

        private static int SleepForLockConfirm = 10 * 1000; // 10 seconds
        private static bool getLock(IXenConnection connection, Session session)
        {
            Dictionary<string, string> config = new Dictionary<string, string>();
            string newUploadLock = System.Configuration.ConfigurationManager.AppSettings["UUID"];
            newUploadLock += "|" + DateTime.UtcNow.ToString();
            config[CallHomeSettings.UPLOAD_LOCK] = newUploadLock;
            Pool.set_gui_config(session, connection.Cache.Pools[0].opaque_ref, config);
            System.Threading.Thread.Sleep(SleepForLockConfirm);
            config = Pool.get_gui_config(session, connection.Cache.Pools[0].opaque_ref);
            return config[CallHomeSettings.UPLOAD_LOCK] == newUploadLock;
        }

        public static bool Request(IXenConnection connection, Session session)
        {
            bool needRetry = false;
            Dictionary<string, string> config = Pool.get_gui_config(session, connection.Cache.Pools[0].opaque_ref);
            if (BoolKey(config, CallHomeSettings.STATUS) == false)
            {
                log.InfoFormat("Will not report for XenServer {0} that was not Enroll", connection.Hostname);
                return false;
            }
            //Check if there already some service doing the uploading already
            if (CanLock(Get(config, CallHomeSettings.UPLOAD_LOCK)) == false)
            {
                log.InfoFormat("Will not report for XenServer {0} that already locked", connection.Hostname);
                return false;
            }

            //No Lock has been set before, Check upload is due
            int intervalInDays = IntKey(config, CallHomeSettings.INTERVAL_IN_DAYS, CallHomeSettings.intervalInDaysDefault);
            DateTime lastSuccessfulUpload = DateTime.UtcNow;
            bool haveSuccessfulUpload = false;
            if (config.ContainsKey(CallHomeSettings.LAST_SUCCESSFUL_UPLOAD))
            {
                try
                {
                    lastSuccessfulUpload = DateTime.Parse(Get(config, CallHomeSettings.LAST_SUCCESSFUL_UPLOAD));
                    haveSuccessfulUpload = true;
                }
                catch (Exception exn)
                {
                    log.Error("Catch exception when Parse LAST_SUCCESSFUL_UPLOAD", exn);
                }
            }

            if (haveSuccessfulUpload)
            {//If last successful update not due. return
                if (DateTime.Compare(DateTime.UtcNow, lastSuccessfulUpload.AddDays(intervalInDays)) < 0)
                {
                    log.InfoFormat("Will not report for XenServer {0} that was not due {1} days", connection.Hostname, intervalInDays);
                    return false;
                }
            }

            if (config.ContainsKey(CallHomeSettings.LAST_FAILED_UPLOAD))
            {
                try
                {
                    DateTime LastFailedUpload = DateTime.Parse(Get(config, CallHomeSettings.LAST_FAILED_UPLOAD));

                    if (haveSuccessfulUpload)
                    {
                        if (DateTime.Compare(lastSuccessfulUpload, LastFailedUpload) > 0)
                            return false; //A retry is not needed
                    }

                    int retryInterval = IntKey(config, CallHomeSettings.RETRY_INTERVAL, CallHomeSettings.RetryIntervalDefault);
                    if (DateTime.Compare(LastFailedUpload.AddDays(retryInterval), DateTime.UtcNow) <= 0)
                    {
                        log.InfoFormat("Retry since retryInterval{0} - {1} > {2} meeted", LastFailedUpload, DateTime.UtcNow, retryInterval);
                        needRetry = true;
                    }
                    else
                        return false;
                }
                catch (Exception exn)
                {
                    log.Error("Catch exception when check if retry was needed", exn);
                }

            }

            DateTime currentTime = DateTime.UtcNow;
            if (!needRetry)
            {//Check if uploading schedule meet only for new upload
                
                DayOfWeek dayOfWeek;
                if (!Enum.TryParse(Get(config, CallHomeSettings.DAY_OF_WEEK), out dayOfWeek))
                {
                    log.Error("DAY_OF_WEEK not existed");
                    return false;
                }
                if (!config.ContainsKey(CallHomeSettings.TIME_OF_DAY))
                {
                    log.Error("TIME_OF_DAY not existed");
                    return false;
                }

                int TimeOfDay = IntKey(config, CallHomeSettings.TIME_OF_DAY, CallHomeSettings.GetDefaultTime());
                if (currentTime.DayOfWeek != dayOfWeek && currentTime.Hour != TimeOfDay)
                {
                    log.InfoFormat("Will not report for XenServer {0} for incorrect schedule", connection.Hostname);
                    return false;
                }
            }

            return getLock(connection, session);
        }

        private static int DemandTimeOutMinutes = 30;
        public static bool OnDemandRequest(IXenConnection connection, Session session)
        {
            Dictionary<string, string> config = Pool.get_gui_config(session, connection.Cache.Pools[0].opaque_ref);
            if (BoolKey(config, CallHomeSettings.STATUS) == false)
            {
                log.InfoFormat("Will not report on demand for XenServer {0} that was not Enroll", connection.Hostname);
                return false;
            }

            //Check if there already some service doing the uploading already
            if (CanLock(Get(config, CallHomeSettings.UPLOAD_LOCK)) == false)
            {
                log.InfoFormat("Will not report for XenServer {0} that already locked", connection.Hostname);
                return false;
            }

            if (config.ContainsKey(CallHomeSettings.NEW_UPLOAD_REQUEST))
            {
                DateTime newUploadRequestDueTime = DateTime.Parse(Get(config, CallHomeSettings.NEW_UPLOAD_REQUEST)).AddMinutes(DemandTimeOutMinutes);
                if (DateTime.Compare(newUploadRequestDueTime, DateTime.UtcNow) >= 0)
                {
                    return getLock(connection, session);
                }
                else
                {
                    log.InfoFormat("Will not report on demand for XenServer {0} since the demand due", connection.Hostname);
                    return false;
                }
            }
            else
                return false;
        }
    }
}