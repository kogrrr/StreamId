namespace Stream.Id {
  using GracenoteSDK;
  using NAudio.CoreAudioApi;
  using NAudio.Wave;
  using System;
  using System.Collections.Generic;
  using System.Configuration;
  using System.IO;
  using System.Linq;
  using System.Net;
  using System.Reflection;
  using System.Threading;

  public static class Program {
    public static bool DidMatch;

    static void Main(string[] args) {
      var name = "Swish Draws Animals";
      var id = -1;
      var offset = TimeSpan.Zero;
      var startTime = DateTime.UtcNow;

      // Name
      if (args.Length > 0) {
        name = args[0];
      }

      // Id
      if (args.Length > 1) {
        if (!int.TryParse(args[1], out id)) {
          Console.WriteLine($"Invalid id: {args[1]}");
        }
      }

      // Time Offset
      if (args.Length > 2) {
        if (!TimeSpan.TryParse(args[2], out offset)) {
          Console.WriteLine($"Invalid offset: {args[2]}");
        }
      }

      Console.WriteLine($"{name} {offset}");

      var clientId = ConfigurationManager.AppSettings["ClientId"];
      var clientIdTag = ConfigurationManager.AppSettings["ClientIdTag"];
      var license = ConfigurationManager.AppSettings["License"];
      var libPath = ConfigurationManager.AppSettings["SDKLibraryPath"];
      var appVersion = Assembly.GetCallingAssembly().GetName().Version.ToString();

      try {
        var gracenote = new Gracenote(clientId, clientIdTag, license, libPath, appVersion.ToString());
        gracenote.Initialize();
        var user = gracenote.User;

        var musicIDStream = new GnMusicIdStream(user, GnMusicIdStreamPreset.kPresetMicrophone, new StreamEventDelgate());
        //musicIDStream.Options().ResultSingle(true);
        musicIDStream.Options().LookupData(GnLookupData.kLookupDataContent, true);

        WasapiLoopbackCapture capture = null;
        while (capture == null) {
          try {
            // Do audio magic (https://github.com/naudio/NAudio/blob/master/Docs/WasapiLoopbackCapture.md)
            capture = new WasapiLoopbackCapture() { ShareMode = AudioClientShareMode.Shared };
          } catch (Exception e) {
            Console.WriteLine($"Unable to begin capture: {e.Message}");
            Thread.Sleep(5);
          }
        }
        var cFormat = capture.WaveFormat;

        // musicIDStream can't handle the 32bit floats from WasapiLoopbackCapture
        var format = new WaveFormat(cFormat.SampleRate, 16, cFormat.Channels);
        musicIDStream.AudioProcessStart((uint)format.SampleRate, (uint)format.BitsPerSample, (uint)format.Channels);

        //var waveOut = new WaveFileWriter("output.wav", new WaveFormat());

        capture.DataAvailable += (_, waveInEvent) => {
          var source = waveInEvent.Buffer;
          var sourceLen = waveInEvent.BytesRecorded;

          if (sourceLen > 0) {
            var dest = new byte[sourceLen / 2];
            for (var i = 0; i < dest.Length / 2; ++i) {
              var temp = BitConverter.ToSingle(source, i * 4);
              var scaled = (short)(temp * 32768); // scale to 16 bits
              var scaledBytes = BitConverter.GetBytes(scaled);
              Array.Copy(scaledBytes, 0, dest, i * 2, scaledBytes.Length);
            }

            musicIDStream.AudioProcess(dest, (uint)dest.Length);
            //waveOut.Write(dest, 0, dest.Length);
            //waveOut.Flush();
          }
        };

        capture.RecordingStopped += (_, stoppedEventArgs) => {
          musicIDStream.AudioProcessStop();
        };

        var stop = false;
        Console.CancelKeyPress += (_, e) => {
          switch (capture.CaptureState) {
            case CaptureState.Capturing:
            case CaptureState.Starting:
              stop = true;
              break;
            default:
              break;
          }
          e.Cancel = true;
        };

        var random = new Random();

        capture.StartRecording();
        while (!stop) {
          Console.WriteLine("Identifying");
          DidMatch = false;
          musicIDStream.IdentifyAlbum();
          Thread.Sleep(1000);

          if (DidMatch) {
            var sleep = random.Next(60, 90);
            Console.WriteLine($"Sleeping for {sleep} seconds.");
            for (var i = 0; i < sleep && !stop; ++i) {
              //Console.Write((i % 15 == 0) ? i.ToString() : ".");
              Thread.Sleep(1000);
              if (Console.KeyAvailable && Console.ReadKey(true).Key == ConsoleKey.I) {
                break;
              }
            }
            Console.WriteLine();
          } else {
            var sleep = random.Next(23, 43);
            Console.WriteLine($"No match. Sleeping {sleep} seconds.");
            for (var i = 0; i < sleep && !stop; ++i) {
              //Console.Write(".");
              Thread.Sleep(1000);
              if (Console.KeyAvailable && Console.ReadKey(true).Key == ConsoleKey.I) {
                break;
              }
            }
            Console.WriteLine();
          }
        }
        capture.StopRecording();
        Thread.Sleep(500);
        capture.Dispose();
        //waveOut.Close();
      } catch (GnException e) {
        Console.WriteLine("GnException :" + e.ErrorDescription + " Code " + e.ErrorCode.ToString("X") + "API:" + e.ErrorAPI);
        Console.ReadKey();
      }
    }
  }

  public class StreamEventDelgate : GnMusicIdStreamEventsDelegate {
    private class Match {
      public Match(GnResponseAlbums result) {
        foreach (var album in result.Albums) {
          Albums.Add(album.GnUId);
          Artists.Add(album.Artist.Contributor.GnUId);

          foreach (var track in album.TracksMatched) {
            Tracks.Add(track.GnUId);
            var trackArtist = track.Artist?.Contributor?.GnUId;
            if (!string.IsNullOrWhiteSpace(trackArtist)) {
              Artists.Add(trackArtist);
            }
          }
        }
      }

      public HashSet<string> Artists { get; set; } = new HashSet<string>();
      public HashSet<string> Albums { get; set; } = new HashSet<string>();
      public HashSet<string> Tracks { get; set; } = new HashSet<string>();

      public bool AreMaybeTheSame(Match match) {
        var matches = Tracks.Intersect(match.Tracks).Count();
        var total = (Tracks.Count + match.Tracks.Count) / 2;
        var ratio = ((double)matches) / total;
        Console.WriteLine($"Match Ratio: {ratio}");
        return ratio > 0.8;
      }
    }

    private Stack<Match> _matches = new Stack<Match>();

    public override void MusicIdStreamAlbumResult(GnResponseAlbums result, IGnCancellable canceller) {
      if (result.ResultCount == 0) {
        Console.WriteLine("Failed to match =(");
        return;
      } else {
        Program.DidMatch = true;
      }

      var match = new Match(result);

      var dupe = _matches.Take(5).Any(x => x.AreMaybeTheSame(match));
      _matches.Push(match);
      if (dupe) {
        Console.WriteLine("Skipping same song.");
        return;
      }


      var first = true;
      foreach (var album in result.Albums) {
        if (first) {
          first = false;
        } else {
          Console.WriteLine();
        }

        var track = album.TracksMatched?.FirstOrDefault();

        var albumArtist = album.Artist.Name.Display;
        var trackArtist = track.Artist?.Name?.Display;
        var artist = string.IsNullOrWhiteSpace(trackArtist) ? albumArtist : trackArtist;
        Console.WriteLine($"\tArtist: {artist}");

        Console.WriteLine($"\tAlbum: {album.Title.Display} ({album.Year})");

        Console.WriteLine($"\tTrack #{track.TrackNumber}: {track.Title.Display}");

        var spotifyUrl = new Uri("https://open.spotify.com/search/results/" + Uri.EscapeUriString($"artist:\"{artist.Replace("\"", "")}\" {track.Title.Display}"));
        Console.WriteLine($"\tSpotify Search: {spotifyUrl}");

        //var content = album.Content(GnContentType.kContentTypeImageCover);

        //if (content != null) {
        //  var extension = "";
        //  switch (content.MimeType) {
        //    case "image/jpeg":
        //      extension = "jpg";
        //      break;
        //    case "image/png":
        //      extension = "png";
        //      break;
        //    default:
        //      Console.WriteLine($"\tUnknown MimeType {content.MimeType}");
        //      break;
        //  }

        //  if (!string.IsNullOrWhiteSpace(extension)) {
        //    Directory.CreateDirectory("content");
        //    var file = new FileInfo($"content\\{album.GnUId}.{content.ContentType}.{extension}");
        //    if (!file.Exists) {
        //      var asset = content.Asset(GnImageSize.kImageSizeXLarge);
        //      var url = asset.UrlHttp();
        //      if (!string.IsNullOrWhiteSpace(url)) {
        //        Console.WriteLine($"\tSaving {url} to {file.FullName}");
        //        var wc = new WebClient();
        //        wc.DownloadFile(url, file.FullName);
        //      }
        //    }
        //  }
        //}
      }
    }

    public override void MusicIdStreamIdentifyCompletedWithError(GnError e) {
      Console.WriteLine($"Error: {e.ErrorAPI()} {e.ErrorModule()} {e.ErrorCode()} {e.ErrorDescription()}");
    }

    public override void MusicIdStreamIdentifyingStatusEvent(GnMusicIdStreamIdentifyingStatus status, IGnCancellable canceller) {
      //Console.WriteLine($"MusicIdStreamIdentifyingStatusEvent ({status})");
    }

    public override void MusicIdStreamProcessingStatusEvent(GnMusicIdStreamProcessingStatus status, IGnCancellable canceller) {
      //Console.WriteLine($"MusicIdStreamProcessingStatusEvent ({status})");
    }

    public override void StatusEvent(GnStatus status, uint percentComplete, uint bytesTotalSent, uint bytesTotalReceived, IGnCancellable canceller) {
      //Console.WriteLine($"StatusEvent ({status}), complete ({percentComplete:D}%), sent ({bytesTotalSent:D}), received ({bytesTotalReceived:D})");
    }
  }
}
