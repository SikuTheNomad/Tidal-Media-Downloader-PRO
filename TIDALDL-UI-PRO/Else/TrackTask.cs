using AIGS.Common;
using AIGS.Helper;
using Stylet;
using System;
using TidalLib;

namespace TIDALDL_UI.Else
{
    public class TrackTask : Screen
    {
        public int    Index { get; set; }
        public string Codec { get; set; }
        public string Title { get; set; }
        public string Own   { get; set; }
        public ProgressHelper Progress { get; set; }

        System.DateTime StartTime { get; set; }
        public string CurSizeString { get; set; } 
        public string TotalSizeString { get; set; }
        public long CountIncreSize { get; set; } = 0;
        public string DownloadSpeedString { get; set; }

        Track TidalTrack { get; set; }
        Album TidalAlbum { get; set; }
        Playlist TidalPlaylist { get; set; }
        StreamUrl Stream { get; set; }
        Settings Settings { get; set; }

        public delegate void TELL_PARENT_OVER();
        public TELL_PARENT_OVER TellParentOver;

        public TrackTask(Track track, int index, Settings settings, TELL_PARENT_OVER tellparent, Album album = null, Playlist playlist = null)
        {
            Index = index;
            Settings = settings;
            TidalTrack = track;
            TidalAlbum = album;
            TidalPlaylist = playlist;

            Title = track.Title;
            Own = track.Album.Title;

            Progress = new ProgressHelper(false);
            TellParentOver = tellparent;

            Start();
        }

        #region Method
        public void Start()
        {
            //Add to threadpool
            ThreadTool.AddWork((object[] data) =>
            {
                if (Progress.GetStatus() != ProgressHelper.STATUS.WAIT)
                    return;

                Progress.SetStatus(ProgressHelper.STATUS.RUNNING);
                Download();
            });
        }

        public void Cancel()
        {
            if (Progress.GetStatus() != ProgressHelper.STATUS.COMPLETE)
                Progress.SetStatus(ProgressHelper.STATUS.CANCLE);
        }

        public void Restart()
        {
            ProgressHelper.STATUS status = Progress.GetStatus();
            if (status == ProgressHelper.STATUS.CANCLE || status == ProgressHelper.STATUS.ERROR)
            {
                Progress.Clear();
                Start();
            }
        }
        #endregion


        

        public void Download()
        {
            try
            {
                LoginKey key = Tools.GetKey();

                //GetStream
                Progress.StatusMsg = "GetStream...";
                (Progress.Errmsg, Stream) = Client.GetTrackStreamUrl(key, TidalTrack.ID, Settings.AudioQuality).Result;
                if (Progress.Errmsg.IsNotBlank() || Stream == null)
                    goto ERR_RETURN;

                Codec = Stream.Codec;
                Progress.StatusMsg = "GetStream success...";

                if (TidalAlbum == null && TidalTrack.Album != null)
                {
                    string tmpmsg;
                    (tmpmsg, TidalAlbum) = Client.GetAlbum(key, TidalTrack.Album.ID, false).Result;
                }

                //Get path 
                string path = Tools.GetTrackPath(Settings, TidalTrack, Stream, TidalAlbum, TidalPlaylist);

                //Check if song downloaded already
                string checkpath = Settings.OnlyM4a ? path.Replace(".mp4", ".m4a") : path;
                if (Settings.CheckExist && System.IO.File.Exists(checkpath))
                {
                    Progress.UpdateInt(100, 100);
                    Progress.SetStatus(ProgressHelper.STATUS.COMPLETE);
                    goto CALL_RETURN;
                }

                //Download
                Progress.StatusMsg = "Start...";
                bool bDownloaded = false;

                if (Stream.SegmentUrls != null && Stream.SegmentUrls.Length > 1)
                {
                    // DASH segmented stream: download all segments and concatenate
                    bDownloaded = DownloadDashSegments(Stream.SegmentUrls, path, key);
                    if (!bDownloaded)
                    {
                        Progress.Errmsg = "DASH segment download failed!";
                        goto ERR_RETURN;
                    }
                }
                else
                {
                    for (int i = 0; i < 50 && Progress.GetStatus() != ProgressHelper.STATUS.CANCLE; i++)
                    {
                        StartTime = TimeHelper.GetCurrentTime();
                        if ((bool)DownloadFileHepler.Start(Stream.Url, path, Timeout: 5 * 1000, UpdateFunc: UpdateDownloadNotify, ErrFunc: ErrDownloadNotify, Proxy: key.Proxy))
                        {
                            bDownloaded = true;
                            break;
                        }
                    }
                    if (!bDownloaded)
                    {
                        Progress.Errmsg = "Download failed!";
                        System.IO.File.Delete(path);
                        goto ERR_RETURN;
                    }
                }

                {
                    //Decrypt (DASH streams are unencrypted; legacy streams may be encrypted)
                    Progress.StatusMsg = "Decrypt...";
                    if (!Tools.DecryptTrackFile(Stream, path))
                    {
                        Progress.Errmsg = "Decrypt failed!";
                        goto ERR_RETURN;
                    }

                    // DASH FLAC: remux mp4 container to .flac
                    if (Stream.SegmentUrls != null && Stream.SegmentUrls.Length > 1
                        && Stream.Codec != null && Stream.Codec.ToUpper() == "FLAC"
                        && path.ToLower().EndsWith(".flac") == false)
                    {
                        (Progress.Errmsg, path) = Tools.ConvertMp4ToFlac(path);
                        if (Progress.Errmsg.IsNotBlank())
                            goto ERR_RETURN;
                    }
                    else if (Settings.OnlyM4a)
                    {
                        (Progress.Errmsg, path) = Tools.ConvertMp4ToM4a(path, Stream);
                        if (Progress.Errmsg.IsNotBlank())
                            goto ERR_RETURN;
                    }

                    //Get lyrics
                    Progress.StatusMsg = "Get lyrics...";
                    string lyrics = Client.GetLyrics(key, TidalTrack.Title, TidalTrack.Artist == null ? "" : TidalTrack.Artist.Name);

                    //SetMetaData
                    Progress.StatusMsg = "Set metaData...";
                    if (TidalAlbum == null)
                        (Progress.Errmsg, TidalAlbum) = Client.GetAlbum(key, TidalTrack.Album.ID, false).Result;
                    Progress.Errmsg = Tools.SetMetaData(path, TidalAlbum, TidalTrack, lyrics);
                    if (Progress.Errmsg.IsNotBlank())
                    {
                        Progress.Errmsg = "Set metadata failed!" + Progress.Errmsg;
                        goto ERR_RETURN;
                    }

                    Progress.SetStatus(ProgressHelper.STATUS.COMPLETE);
                    goto CALL_RETURN;
                }
            }
            catch(Exception e)
            {
                Progress.Errmsg = "Download failed!" + e.Message;
            }

        ERR_RETURN:
            if (Progress.GetStatus() == ProgressHelper.STATUS.CANCLE)
                goto CALL_RETURN;
            Progress.SetStatus(ProgressHelper.STATUS.ERROR);

        CALL_RETURN:
            TellParentOver();

            DownloadSpeedString = "";
        }

        private bool DownloadDashSegments(string[] segmentUrls, string outputPath, LoginKey key)
        {
            try
            {
                string tempDir = System.IO.Path.GetTempPath();
                string baseName = System.IO.Path.GetFileNameWithoutExtension(outputPath);
                var segPaths = new System.Collections.Generic.List<string>();

                for (int s = 0; s < segmentUrls.Length; s++)
                {
                    if (Progress.GetStatus() == ProgressHelper.STATUS.CANCLE)
                        return false;

                    string segPath = System.IO.Path.Combine(tempDir, $"{baseName}_seg{s}.tmp");
                    Progress.StatusMsg = $"Downloading segment {s + 1}/{segmentUrls.Length}...";

                    bool ok = false;
                    for (int retry = 0; retry < 5 && !ok; retry++)
                    {
                        ok = (bool)DownloadFileHepler.Start(segmentUrls[s], segPath, Timeout: 30 * 1000, Proxy: key.Proxy);
                    }
                    if (!ok)
                    {
                        foreach (var p in segPaths) try { System.IO.File.Delete(p); } catch { }
                        return false;
                    }
                    segPaths.Add(segPath);

                    Progress.UpdateInt(s + 1, segmentUrls.Length);
                }

                using (var outStream = new System.IO.FileStream(outputPath, System.IO.FileMode.Create))
                {
                    foreach (string segPath in segPaths)
                    {
                        byte[] data = System.IO.File.ReadAllBytes(segPath);
                        outStream.Write(data, 0, data.Length);
                        System.IO.File.Delete(segPath);
                    }
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        public void ErrDownloadNotify(long lTotalSize, long lAlreadyDownloadSize, string sErrMsg, object data)
        {
            Progress.Errmsg = sErrMsg;
            return;
        }

        public bool UpdateDownloadNotify(long lTotalSize, long lAlreadyDownloadSize, long lIncreSize, object data)
        {
            Progress.UpdateInt(lAlreadyDownloadSize, lTotalSize);
            if (Progress.GetStatus() != ProgressHelper.STATUS.RUNNING)
                return false;

            CountIncreSize += lIncreSize;
            long consumeTime = TimeHelper.CalcConsumeTime(StartTime);

            if (consumeTime >= 1000)
            {
                DownloadSpeedString = AIGS.Common.Convert.ConverStorageUintToString(CountIncreSize, AIGS.Common.Convert.UnitType.BYTE) + "/S";
                CountIncreSize = 0;
                StartTime = TimeHelper.GetCurrentTime();
            }

            CurSizeString = AIGS.Common.Convert.ConverStorageUintToString(lAlreadyDownloadSize, AIGS.Common.Convert.UnitType.BYTE);
            if (TotalSizeString.IsBlank())
                TotalSizeString = AIGS.Common.Convert.ConverStorageUintToString(lTotalSize, AIGS.Common.Convert.UnitType.BYTE);
            return true;
        }
    }
}
