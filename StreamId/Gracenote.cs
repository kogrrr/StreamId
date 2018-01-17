namespace Stream.Id
{
    using GracenoteSDK;
    using System;
    using System.IO;

    public class Gracenote
    {
        public const string UserFileName = "user.txt";

        public string ClientId { get; private set; }
        public string ClientIdTag { get; private set; }
        public string License { get; private set; }
        public string LibraryPath { get; private set; }
        public string ApplicationVersion { get; private set; }

        public GnLookupMode LookupMode { get; } = GnLookupMode.kLookupModeOnline;

        private Lazy<GnUser> _user;
        public GnUser User => _user.Value;

        private GnManager _manager;

        public Gracenote(string clientId, string clientIdTag, string license, string libraryPath, string appVersion)
        {
            ClientId = clientId;
            ClientIdTag = clientIdTag;
            License = license;
            LibraryPath = libraryPath;
            ApplicationVersion = appVersion;

            _user = new Lazy<GnUser>(GetUser, false);
        }

        public void Initialize()
        {
            if (_manager != null)
            {
                throw new InvalidOperationException("You can only initialize a Gracenote object once.");
            }

            /* Initialize SDK */
            _manager = new GnManager(LibraryPath, License, GnLicenseInputMode.kLicenseInputModeString);

            // Instantiate SQLite module to use as our database 
            var storageSqlite = GnStorageSqlite.Enable();

            // Set folder location for sqlite storage 
            storageSqlite.StorageLocation = ".";
        }

        private GnUser GetUser()
        {
            if(_manager == null)
            {
                throw new Exception("Gracenote is not initialized.");
            }

            GnUser user = null;

            var userRegMode = GnUserRegisterMode.kUserRegisterModeOnline;

            // read stored user data from file 
            if (File.Exists(UserFileName))
            {
                var serializedUser = File.ReadAllText(UserFileName);
                if (!string.IsNullOrWhiteSpace(serializedUser))
                {
                    // pass in clientID (optional) to ensure this serialized user is for this clientID 
                    user = new GnUser(serializedUser, ClientId);
                }
            }

            if (user == null)
            {
                // Register new user
                var serializedUser = _manager.UserRegister(userRegMode, ClientId, ClientIdTag, ApplicationVersion).c_str();

                // store user data to file
                File.WriteAllText(UserFileName, serializedUser);

                user = new GnUser(serializedUser);
            }

            if (user != null)
            {
                // set user to match our desired lookup mode (all queries done with this user will inherit the lookup mode) 
                user.Options().LookupMode(LookupMode);

                var locale = new GnLocale(
                    GnLocaleGroup.kLocaleGroupMusic,
                    GnLanguage.kLanguageEnglish,
                    GnRegion.kRegionDefault,
                    GnDescriptor.kDescriptorSimplified,
                    user,
                    null
                    );

                locale.SetGroupDefault();

                return user;
            }
            else
            {
                throw new Exception("Failed to create user!");
            }
        }
    }
}
