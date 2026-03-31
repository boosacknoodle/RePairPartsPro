using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using RepairPartsPro.Models;

namespace RepairPartsPro.Services;

public sealed class VendorDataStore
{
    private readonly string _connectionString;

    public VendorDataStore(IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString("DefaultConnection") ?? "Data Source=repairpartspro.db";

        var builder = new SqliteConnectionStringBuilder(_connectionString);
        if (!string.IsNullOrWhiteSpace(builder.DataSource))
        {
            var fullPath = Path.GetFullPath(builder.DataSource);
            var directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }
        }
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var createOffers = connection.CreateCommand();
        createOffers.CommandText = """
            CREATE TABLE IF NOT EXISTS VendorPartOffers (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Title TEXT NOT NULL,
                Brand TEXT NOT NULL,
                Model TEXT NOT NULL,
                PartType TEXT NOT NULL,
                BasePartNumber TEXT NOT NULL,
                Marketplace TEXT NOT NULL,
                Price REAL NOT NULL,
                Currency TEXT NOT NULL,
                ListingUrl TEXT NOT NULL,
                ImageUrl1 TEXT NOT NULL,
                ImageUrl2 TEXT NOT NULL,
                ImageUrl3 TEXT NOT NULL,
                IsGenuineSupplier INTEGER NOT NULL,
                SourceVerifiedAtUtc TEXT NOT NULL,
                SourceUrl TEXT NOT NULL DEFAULT '',
                SourcePriceText TEXT NOT NULL DEFAULT '',
                SourceType TEXT NOT NULL DEFAULT ''
            );
        """;
        await createOffers.ExecuteNonQueryAsync(cancellationToken);

        var addSourceUrlColumn = connection.CreateCommand();
        addSourceUrlColumn.CommandText = "ALTER TABLE VendorPartOffers ADD COLUMN SourceUrl TEXT NOT NULL DEFAULT ''";
        try
        {
            await addSourceUrlColumn.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 1 && ex.Message.Contains("duplicate column name", StringComparison.OrdinalIgnoreCase))
        {
            // Column already exists in upgraded databases.
        }

        var addSourcePriceTextColumn = connection.CreateCommand();
        addSourcePriceTextColumn.CommandText = "ALTER TABLE VendorPartOffers ADD COLUMN SourcePriceText TEXT NOT NULL DEFAULT ''";
        try
        {
            await addSourcePriceTextColumn.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 1 && ex.Message.Contains("duplicate column name", StringComparison.OrdinalIgnoreCase))
        {
            // Column already exists in upgraded databases.
        }

        var addSourceTypeColumn = connection.CreateCommand();
        addSourceTypeColumn.CommandText = "ALTER TABLE VendorPartOffers ADD COLUMN SourceType TEXT NOT NULL DEFAULT ''";
        try
        {
            await addSourceTypeColumn.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 1 && ex.Message.Contains("duplicate column name", StringComparison.OrdinalIgnoreCase))
        {
            // Column already exists in upgraded databases.
        }

        // Migration safety: remove legacy duplicates before creating a unique index.
        var dedupe = connection.CreateCommand();
        dedupe.CommandText = """
            DELETE FROM VendorPartOffers
            WHERE Id NOT IN (
                SELECT MAX(Id)
                FROM VendorPartOffers
                GROUP BY BasePartNumber, Marketplace
            );
        """;
        await dedupe.ExecuteNonQueryAsync(cancellationToken);

        var createUniqueOffer = connection.CreateCommand();
        createUniqueOffer.CommandText = """
            CREATE UNIQUE INDEX IF NOT EXISTS IX_VendorPartOffers_BasePart_Marketplace
            ON VendorPartOffers (BasePartNumber, Marketplace);
        """;
        await createUniqueOffer.ExecuteNonQueryAsync(cancellationToken);

        var createCertifications = connection.CreateCommand();
        createCertifications.CommandText = """
            CREATE TABLE IF NOT EXISTS PriceCertificationRecords (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                OfferId INTEGER NOT NULL,
                IsCertified INTEGER NOT NULL,
                Reason TEXT NOT NULL,
                CertifiedAtUtc TEXT NOT NULL
            );
        """;
        await createCertifications.ExecuteNonQueryAsync(cancellationToken);

        // Safety cleanup for previously scraped noise values.
        var invalidateSuspiciousPrices = connection.CreateCommand();
        invalidateSuspiciousPrices.CommandText = """
            UPDATE VendorPartOffers
            SET SourceVerifiedAtUtc = '2000-01-01T00:00:00.0000000Z'
            WHERE Price < 30 OR Price > 10000;
        """;
        await invalidateSuspiciousPrices.ExecuteNonQueryAsync(cancellationToken);

        var createUsers = connection.CreateCommand();
        createUsers.CommandText = """
            CREATE TABLE IF NOT EXISTS Users (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Email TEXT NOT NULL COLLATE NOCASE,
                PasswordHash TEXT NOT NULL,
                CreatedAtUtc TEXT NOT NULL
            );
        """;
        await createUsers.ExecuteNonQueryAsync(cancellationToken);

        var createUsersIndex = connection.CreateCommand();
        createUsersIndex.CommandText = "CREATE UNIQUE INDEX IF NOT EXISTS IX_Users_Email ON Users (Email COLLATE NOCASE);";
        await createUsersIndex.ExecuteNonQueryAsync(cancellationToken);

            // Migrate: add StripeCustomerId column if not present
            var addStripeCol = connection.CreateCommand();
            addStripeCol.CommandText = "ALTER TABLE Users ADD COLUMN StripeCustomerId TEXT;";
            try { await addStripeCol.ExecuteNonQueryAsync(cancellationToken); } catch { /* column already exists */ }

        var createSubscriptions = connection.CreateCommand();
        createSubscriptions.CommandText = """
            CREATE TABLE IF NOT EXISTS UserSubscriptions (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                UserId INTEGER NOT NULL UNIQUE,
                TierName TEXT NOT NULL,
                StartedAtUtc TEXT NOT NULL,
                ExpiresAtUtc TEXT,
                IsActive INTEGER NOT NULL,
                FOREIGN KEY (UserId) REFERENCES Users(Id)
            );
        """;
        await createSubscriptions.ExecuteNonQueryAsync(cancellationToken);

        var createPasswordResetTokens = connection.CreateCommand();
        createPasswordResetTokens.CommandText = """
            CREATE TABLE IF NOT EXISTS PasswordResetTokens (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                UserId INTEGER NOT NULL,
                TokenHash TEXT NOT NULL UNIQUE,
                ExpiresAtUtc TEXT NOT NULL,
                UsedAtUtc TEXT,
                CreatedAtUtc TEXT NOT NULL,
                FOREIGN KEY (UserId) REFERENCES Users(Id)
            );
        """;
        await createPasswordResetTokens.ExecuteNonQueryAsync(cancellationToken);

        var createSearchAnalytics = connection.CreateCommand();
        createSearchAnalytics.CommandText = """
            CREATE TABLE IF NOT EXISTS SearchAnalytics (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                UserId INTEGER NOT NULL,
                QueryText TEXT NOT NULL,
                HasResults INTEGER NOT NULL,
                CreatedAtUtc TEXT NOT NULL,
                FOREIGN KEY (UserId) REFERENCES Users(Id)
            );
        """;
        await createSearchAnalytics.ExecuteNonQueryAsync(cancellationToken);

        var createPlanClickEvents = connection.CreateCommand();
        createPlanClickEvents.CommandText = """
            CREATE TABLE IF NOT EXISTS PlanClickEvents (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                UserId INTEGER NOT NULL,
                TierName TEXT NOT NULL,
                CreatedAtUtc TEXT NOT NULL,
                FOREIGN KEY (UserId) REFERENCES Users(Id)
            );
        """;
        await createPlanClickEvents.ExecuteNonQueryAsync(cancellationToken);

        var seedSubscriptions = connection.CreateCommand();
        seedSubscriptions.CommandText = """
            INSERT OR IGNORE INTO UserSubscriptions (UserId, TierName, StartedAtUtc, ExpiresAtUtc, IsActive)
            SELECT Id, 'Basic', CreatedAtUtc, NULL, 1
            FROM Users
            WHERE Id NOT IN (SELECT UserId FROM UserSubscriptions);
        """;
        await seedSubscriptions.ExecuteNonQueryAsync(cancellationToken);

        var migrateLegacyFreeTier = connection.CreateCommand();
        migrateLegacyFreeTier.CommandText = """
            UPDATE UserSubscriptions
            SET TierName = 'Basic'
            WHERE TierName = 'Free';
        """;
        await migrateLegacyFreeTier.ExecuteNonQueryAsync(cancellationToken);

        var createHardPartIntel = connection.CreateCommand();
        createHardPartIntel.CommandText = """
            CREATE TABLE IF NOT EXISTS HardPartIntel (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                PartName TEXT NOT NULL,
                DeviceFamily TEXT NOT NULL,
                PainReason TEXT NOT NULL,
                SearchHint TEXT NOT NULL,
                ForumSignalSource TEXT NOT NULL,
                PainScore INTEGER NOT NULL,
                IsScamSensitive INTEGER NOT NULL,
                UNIQUE(PartName, DeviceFamily)
            );
        """;
        await createHardPartIntel.ExecuteNonQueryAsync(cancellationToken);

        var seedHardPartIntel = connection.CreateCommand();
        seedHardPartIntel.CommandText = """
            INSERT OR IGNORE INTO HardPartIntel
                (PartName, DeviceFamily, PainReason, SearchHint, ForumSignalSource, PainScore, IsScamSensitive)
            VALUES
                ('USB-C PD Controller (CD3217 / TPS6598x family)', 'MacBook logic boards', 'Frequent charging/no-power failures and limited trusted chip supply', 'MacBook CD3217 USB-C controller original', 'Badcaps laptop troubleshooting patterns', 94, 1),
                ('M92T36 Charging IC', 'Nintendo Switch', 'Often blamed in no-charge/no-power repairs and widely counterfeited', 'Nintendo Switch M92T36 genuine IC', 'Independent console repair forum patterns', 92, 1),
                ('Tristar/U2 charging IC', 'iPhone board repair', 'Common high-demand board-level replacement with fake-marked lots', 'iPhone Tristar U2 original chip', 'Micro-soldering shop complaint patterns', 90, 1),
                ('eDP/LCD display flex cable assembly', 'Ultrabooks and 2-in-1 laptops', 'Model-specific cable variants are hard to source and often mislabeled', 'OEM eDP display flex cable exact model', 'Laptop repair forum recurring posts', 86, 1),
                ('Keyboard ribbon connector and latch set', 'Gaming laptops', 'Connector breakage is common and exact pitch variants are hard to identify', 'keyboard ribbon connector exact board model', 'Badcaps connector failure threads', 84, 1),
                ('DC-in power jack harness', 'Dell/HP/Lenovo laptops', 'Small harness variants frequently mismatch despite similar names', 'OEM DC jack harness exact part number', 'Shop intake complaint trends', 82, 1),
                ('Backlight boost/fuse components', 'MacBook and premium laptops', 'No-backlight repairs require exact tiny components with traceable provenance', 'backlight fuse boost IC exact board number', 'Board-level repair forum pain points', 88, 1),
                ('Boardview-confirmed VRM MOSFET pair', 'GPU and workstation boards', 'MOSFET substitutions cause unstable power behavior and returns', 'VRM MOSFET pair genuine exact marking code', 'High-end board repair complaint patterns', 85, 1),
                ('Southbridge/PCH BGA replacement chip', 'Laptop motherboards', 'Board-level failures require exact donor or trusted new old stock chips', 'PCH BGA original chip exact board family', 'Long-form board repair logs', 91, 1),
                ('USB retimer/redriver IC', 'Thunderbolt/USB4 laptops', 'Signal chips are often remarked and counterfeit in mixed lots', 'USB retimer IC original marking code', 'Repair forum sourcing warnings', 89, 1),
                ('Nintendo Switch BQ24193 charging IC', 'Nintendo Switch', 'Common no-charge repairs with fake batches in circulation', 'BQ24193 genuine IC Nintendo Switch', 'Console repair bench reports', 90, 1),
                ('NAND storage daughterboard', 'Game consoles', 'Donor compatibility and firmware pairing make sourcing difficult', 'console NAND daughterboard exact revision', 'Console board-swap discussions', 83, 1),
                ('Laptop touchpad flex cable', 'Ultrabooks', 'Minor revision changes break compatibility despite similar SKU naming', 'touchpad flex cable exact laptop revision', 'High-volume shop replacement pain', 81, 1),
                ('OLED display driver IC', 'Phone and handheld devices', 'Frequent counterfeit risk with relabeled chips', 'OLED driver IC original reel code', 'Microsoldering failure analysis threads', 90, 1),
                ('Wi-Fi/Bluetooth combo module', 'Laptops and mini PCs', 'OEM-specific whitelist modules are hard to source genuinely', 'OEM whitelisted wifi bluetooth module part number', 'Repair compatibility databases', 80, 1),
                ('Audio codec IC', 'Laptop motherboards', 'Tiny QFN packages are commonly pulled and resold as new', 'audio codec IC new genuine exact package', 'Board-level diagnostics communities', 84, 1),
                ('GPU memory GDDR6 pair-matched set', 'Graphics cards', 'Mismatch lots cause instability and RMA returns', 'GDDR6 matched memory chips genuine lot', 'GPU repair sourcing channels', 88, 1),
                ('Laptop hinge+display side bracket set', 'Premium notebooks', 'Model-year metal bracket changes create high mismatch risk', 'laptop hinge bracket set exact model year', 'Break/fix fleet maintenance reports', 78, 0),
                ('Embedded controller (EC) chip', 'Laptop motherboards', 'EC chips often require exact family/version and trusted source', 'EC controller chip exact board code', 'Firmware repair troubleshooting threads', 87, 1);
        """;
        await seedHardPartIntel.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task SeedIfEmptyAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var countCommand = connection.CreateCommand();
        countCommand.CommandText = "SELECT COUNT(1) FROM VendorPartOffers";
        var existingCount = Convert.ToInt32(await countCommand.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        if (existingCount > 0)
        {
            return;
        }

        var now = DateTime.UtcNow;
        var offers = BuildSeedOffers(now);
        foreach (var offer in offers)
        {
            var insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO VendorPartOffers (
                    Title, Brand, Model, PartType, BasePartNumber,
                    Marketplace, Price, Currency, ListingUrl,
                    ImageUrl1, ImageUrl2, ImageUrl3,
                    IsGenuineSupplier, SourceVerifiedAtUtc,
                    SourceUrl, SourcePriceText, SourceType)
                VALUES (
                    $title, $brand, $model, $partType, $basePartNumber,
                    $marketplace, $price, $currency, $listingUrl,
                    $image1, $image2, $image3,
                    $isGenuine, $verifiedAtUtc,
                    $sourceUrl, $sourcePriceText, $sourceType
                )
                ON CONFLICT(BasePartNumber, Marketplace)
                DO UPDATE SET
                    Title = excluded.Title,
                    Brand = excluded.Brand,
                    Model = excluded.Model,
                    PartType = excluded.PartType,
                    Price = excluded.Price,
                    Currency = excluded.Currency,
                    ListingUrl = excluded.ListingUrl,
                    ImageUrl1 = excluded.ImageUrl1,
                    ImageUrl2 = excluded.ImageUrl2,
                    ImageUrl3 = excluded.ImageUrl3,
                    IsGenuineSupplier = excluded.IsGenuineSupplier,
                        SourceVerifiedAtUtc = excluded.SourceVerifiedAtUtc,
                        SourceUrl = excluded.SourceUrl,
                        SourcePriceText = excluded.SourcePriceText,
                        SourceType = excluded.SourceType;
            """;

            insert.Parameters.AddWithValue("$title", offer.Title);
            insert.Parameters.AddWithValue("$brand", offer.Brand);
            insert.Parameters.AddWithValue("$model", offer.Model);
            insert.Parameters.AddWithValue("$partType", offer.PartType);
            insert.Parameters.AddWithValue("$basePartNumber", offer.BasePartNumber);
            insert.Parameters.AddWithValue("$marketplace", offer.Marketplace);
            insert.Parameters.AddWithValue("$price", offer.Price);
            insert.Parameters.AddWithValue("$currency", offer.Currency);
            insert.Parameters.AddWithValue("$listingUrl", offer.ListingUrl);
            insert.Parameters.AddWithValue("$image1", offer.ImageUrl1);
            insert.Parameters.AddWithValue("$image2", offer.ImageUrl2);
            insert.Parameters.AddWithValue("$image3", offer.ImageUrl3);
            insert.Parameters.AddWithValue("$isGenuine", offer.IsGenuineSupplier ? 1 : 0);
            insert.Parameters.AddWithValue("$verifiedAtUtc", offer.SourceVerifiedAtUtc.ToString("O", CultureInfo.InvariantCulture));
            insert.Parameters.AddWithValue("$sourceUrl", offer.SourceUrl);
            insert.Parameters.AddWithValue("$sourcePriceText", offer.SourcePriceText);
            insert.Parameters.AddWithValue("$sourceType", offer.SourceType);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    public async Task EnsureMarketplaceCoverageAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var now = DateTime.UtcNow;
        var offers = BuildSeedOffers(now);
        foreach (var offer in offers)
        {
            var insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT OR IGNORE INTO VendorPartOffers (
                    Title, Brand, Model, PartType, BasePartNumber,
                    Marketplace, Price, Currency, ListingUrl,
                    ImageUrl1, ImageUrl2, ImageUrl3,
                    IsGenuineSupplier, SourceVerifiedAtUtc,
                    SourceUrl, SourcePriceText, SourceType)
                VALUES (
                    $title, $brand, $model, $partType, $basePartNumber,
                    $marketplace, $price, $currency, $listingUrl,
                    $image1, $image2, $image3,
                    $isGenuine, $verifiedAtUtc,
                    $sourceUrl, $sourcePriceText, $sourceType
                );
            """;

            insert.Parameters.AddWithValue("$title", offer.Title);
            insert.Parameters.AddWithValue("$brand", offer.Brand);
            insert.Parameters.AddWithValue("$model", offer.Model);
            insert.Parameters.AddWithValue("$partType", offer.PartType);
            insert.Parameters.AddWithValue("$basePartNumber", offer.BasePartNumber);
            insert.Parameters.AddWithValue("$marketplace", offer.Marketplace);
            insert.Parameters.AddWithValue("$price", offer.Price);
            insert.Parameters.AddWithValue("$currency", offer.Currency);
            insert.Parameters.AddWithValue("$listingUrl", offer.ListingUrl);
            insert.Parameters.AddWithValue("$image1", offer.ImageUrl1);
            insert.Parameters.AddWithValue("$image2", offer.ImageUrl2);
            insert.Parameters.AddWithValue("$image3", offer.ImageUrl3);
            insert.Parameters.AddWithValue("$isGenuine", offer.IsGenuineSupplier ? 1 : 0);
            insert.Parameters.AddWithValue("$verifiedAtUtc", offer.SourceVerifiedAtUtc.ToString("O", CultureInfo.InvariantCulture));
            insert.Parameters.AddWithValue("$sourceUrl", offer.SourceUrl);
            insert.Parameters.AddWithValue("$sourcePriceText", offer.SourcePriceText);
            insert.Parameters.AddWithValue("$sourceType", offer.SourceType);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    public async Task<IReadOnlyList<VendorPartOffer>> SearchOffersAsync(PartSearchRequest request, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, Title, Brand, Model, PartType, BasePartNumber, Marketplace, Price,
                     Currency, ListingUrl, ImageUrl1, ImageUrl2, ImageUrl3, IsGenuineSupplier, SourceVerifiedAtUtc,
                     SourceUrl, SourcePriceText, SourceType
            FROM VendorPartOffers
            WHERE Brand LIKE $brand
              AND Model LIKE $model
              AND PartType LIKE $partType
            ORDER BY Marketplace, Price;
        """;
        command.Parameters.AddWithValue("$brand", LikeFilter(request.Brand));
        command.Parameters.AddWithValue("$model", LikeFilter(request.Model));
        command.Parameters.AddWithValue("$partType", LikeFilter(request.PartType));

        var offers = new List<VendorPartOffer>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            offers.Add(new VendorPartOffer
            {
                Id = reader.GetInt32(0),
                Title = reader.GetString(1),
                Brand = reader.GetString(2),
                Model = reader.GetString(3),
                PartType = reader.GetString(4),
                BasePartNumber = reader.GetString(5),
                Marketplace = reader.GetString(6),
                Price = reader.GetDecimal(7),
                Currency = reader.GetString(8),
                ListingUrl = reader.GetString(9),
                ImageUrl1 = reader.GetString(10),
                ImageUrl2 = reader.GetString(11),
                ImageUrl3 = reader.GetString(12),
                IsGenuineSupplier = reader.GetInt32(13) == 1,
                SourceVerifiedAtUtc = DateTime.Parse(reader.GetString(14), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                SourceUrl = reader.GetString(15),
                SourcePriceText = reader.GetString(16),
                SourceType = reader.GetString(17)
            });
        }

        if (offers.Count == 0)
        {
            return await GetTopOffersAsync(20, cancellationToken);
        }

        return offers;
    }

    public async Task<IReadOnlyList<VendorPartOffer>> GetTopOffersAsync(int count, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, Title, Brand, Model, PartType, BasePartNumber, Marketplace, Price,
                     Currency, ListingUrl, ImageUrl1, ImageUrl2, ImageUrl3, IsGenuineSupplier, SourceVerifiedAtUtc,
                     SourceUrl, SourcePriceText, SourceType
            FROM VendorPartOffers
            ORDER BY SourceVerifiedAtUtc DESC, Price ASC
            LIMIT $count;
        """;
        command.Parameters.AddWithValue("$count", count);

        var offers = new List<VendorPartOffer>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            offers.Add(new VendorPartOffer
            {
                Id = reader.GetInt32(0),
                Title = reader.GetString(1),
                Brand = reader.GetString(2),
                Model = reader.GetString(3),
                PartType = reader.GetString(4),
                BasePartNumber = reader.GetString(5),
                Marketplace = reader.GetString(6),
                Price = reader.GetDecimal(7),
                Currency = reader.GetString(8),
                ListingUrl = reader.GetString(9),
                ImageUrl1 = reader.GetString(10),
                ImageUrl2 = reader.GetString(11),
                ImageUrl3 = reader.GetString(12),
                IsGenuineSupplier = reader.GetInt32(13) == 1,
                SourceVerifiedAtUtc = DateTime.Parse(reader.GetString(14), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                SourceUrl = reader.GetString(15),
                SourcePriceText = reader.GetString(16),
                SourceType = reader.GetString(17)
            });
        }

        return offers;
    }

    public async Task SaveCertificationAsync(int offerId, bool isCertified, string reason, DateTime certifiedAtUtc, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO PriceCertificationRecords (OfferId, IsCertified, Reason, CertifiedAtUtc)
            VALUES ($offerId, $isCertified, $reason, $certifiedAtUtc);
        """;
        command.Parameters.AddWithValue("$offerId", offerId);
        command.Parameters.AddWithValue("$isCertified", isCertified ? 1 : 0);
        command.Parameters.AddWithValue("$reason", reason);
        command.Parameters.AddWithValue("$certifiedAtUtc", certifiedAtUtc.ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpdateOfferQuoteAsync(int offerId, MarketplaceQuote quote, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE VendorPartOffers
            SET Price = $price,
                Currency = $currency,
                SourceVerifiedAtUtc = $verifiedAtUtc,
                SourceUrl = $sourceUrl,
                SourcePriceText = $sourcePriceText,
                SourceType = $sourceType
            WHERE Id = $offerId;
        """;
        command.Parameters.AddWithValue("$offerId", offerId);
        command.Parameters.AddWithValue("$price", quote.Price);
        command.Parameters.AddWithValue("$currency", quote.Currency);
        command.Parameters.AddWithValue("$verifiedAtUtc", quote.RetrievedAtUtc.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$sourceUrl", quote.ListingUrl);
        command.Parameters.AddWithValue("$sourcePriceText", quote.SourcePriceText);
        command.Parameters.AddWithValue("$sourceType", quote.SourceType);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<AppUser?> FindUserByEmailAsync(string email, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT Id, Email, PasswordHash, CreatedAtUtc FROM Users WHERE Email = $email LIMIT 1";
        cmd.Parameters.AddWithValue("$email", email.Trim());

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;

        return new AppUser
        {
            Id = reader.GetInt32(0),
            Email = reader.GetString(1),
            PasswordHash = reader.GetString(2),
            CreatedAtUtc = DateTime.Parse(reader.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
        };
    }

    public async Task<string?> CreatePasswordResetTokenAsync(string email, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var userLookup = connection.CreateCommand();
        userLookup.CommandText = "SELECT Id FROM Users WHERE Email = $email LIMIT 1";
        userLookup.Parameters.AddWithValue("$email", email.Trim());
        var found = await userLookup.ExecuteScalarAsync(cancellationToken);
        if (found is null or DBNull)
        {
            return null;
        }

        var userId = Convert.ToInt32(found, CultureInfo.InvariantCulture);
        var tokenBytes = RandomNumberGenerator.GetBytes(32);
        var rawToken = Convert.ToBase64String(tokenBytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        var tokenHash = HashToken(rawToken);
        var now = DateTime.UtcNow;
        var expires = now.AddMinutes(30);

        var invalidateOld = connection.CreateCommand();
        invalidateOld.CommandText = "UPDATE PasswordResetTokens SET UsedAtUtc = $usedAt WHERE UserId = $userId AND UsedAtUtc IS NULL";
        invalidateOld.Parameters.AddWithValue("$usedAt", now.ToString("O", CultureInfo.InvariantCulture));
        invalidateOld.Parameters.AddWithValue("$userId", userId);
        await invalidateOld.ExecuteNonQueryAsync(cancellationToken);

        var insert = connection.CreateCommand();
        insert.CommandText = """
            INSERT INTO PasswordResetTokens (UserId, TokenHash, ExpiresAtUtc, UsedAtUtc, CreatedAtUtc)
            VALUES ($userId, $tokenHash, $expires, NULL, $created);
        """;
        insert.Parameters.AddWithValue("$userId", userId);
        insert.Parameters.AddWithValue("$tokenHash", tokenHash);
        insert.Parameters.AddWithValue("$expires", expires.ToString("O", CultureInfo.InvariantCulture));
        insert.Parameters.AddWithValue("$created", now.ToString("O", CultureInfo.InvariantCulture));
        await insert.ExecuteNonQueryAsync(cancellationToken);

        return rawToken;
    }

    public async Task<bool> ResetPasswordWithTokenAsync(string token, string newPasswordHash, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var tokenHash = HashToken(token);
        var now = DateTime.UtcNow;

        var find = connection.CreateCommand();
        find.CommandText = """
            SELECT Id, UserId, ExpiresAtUtc
            FROM PasswordResetTokens
            WHERE TokenHash = $tokenHash
              AND UsedAtUtc IS NULL
            LIMIT 1;
        """;
        find.Parameters.AddWithValue("$tokenHash", tokenHash);

        await using var reader = await find.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return false;
        }

        var resetId = reader.GetInt32(0);
        var userId = reader.GetInt32(1);
        var expiresAt = DateTime.Parse(reader.GetString(2), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        if (expiresAt <= now)
        {
            return false;
        }

        var updatePassword = connection.CreateCommand();
        updatePassword.CommandText = "UPDATE Users SET PasswordHash = $hash WHERE Id = $userId";
        updatePassword.Parameters.AddWithValue("$hash", newPasswordHash);
        updatePassword.Parameters.AddWithValue("$userId", userId);
        await updatePassword.ExecuteNonQueryAsync(cancellationToken);

        var markUsed = connection.CreateCommand();
        markUsed.CommandText = "UPDATE PasswordResetTokens SET UsedAtUtc = $usedAt WHERE Id = $id";
        markUsed.Parameters.AddWithValue("$usedAt", now.ToString("O", CultureInfo.InvariantCulture));
        markUsed.Parameters.AddWithValue("$id", resetId);
        await markUsed.ExecuteNonQueryAsync(cancellationToken);

        return true;
    }

    public async Task TrackSearchAnalyticsAsync(int userId, string queryText, bool hasResults, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO SearchAnalytics (UserId, QueryText, HasResults, CreatedAtUtc)
            VALUES ($userId, $query, $hasResults, $createdAt);
        """;
        cmd.Parameters.AddWithValue("$userId", userId);
        cmd.Parameters.AddWithValue("$query", queryText.Trim());
        cmd.Parameters.AddWithValue("$hasResults", hasResults ? 1 : 0);
        cmd.Parameters.AddWithValue("$createdAt", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task TrackPlanClickAsync(int userId, string tierName, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO PlanClickEvents (UserId, TierName, CreatedAtUtc)
            VALUES ($userId, $tierName, $createdAt);
        """;
        cmd.Parameters.AddWithValue("$userId", userId);
        cmd.Parameters.AddWithValue("$tierName", tierName);
        cmd.Parameters.AddWithValue("$createdAt", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<AnalyticsSummary> GetAnalyticsSummaryAsync(int daysWindow = 30, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var since = DateTime.UtcNow.AddDays(-Math.Abs(daysWindow)).ToString("O", CultureInfo.InvariantCulture);

        var summary = new AnalyticsSummary { DaysWindow = Math.Abs(daysWindow) };

        var totalsCmd = connection.CreateCommand();
        totalsCmd.CommandText = """
            SELECT
                COUNT(1) AS TotalSearches,
                SUM(CASE WHEN HasResults = 0 THEN 1 ELSE 0 END) AS NoResultSearches,
                COUNT(DISTINCT UserId) AS UniqueUsers
            FROM SearchAnalytics
            WHERE CreatedAtUtc >= $since;
        """;
        totalsCmd.Parameters.AddWithValue("$since", since);
        await using (var reader = await totalsCmd.ExecuteReaderAsync(cancellationToken))
        {
            if (await reader.ReadAsync(cancellationToken))
            {
                summary.TotalSearches = reader.IsDBNull(0) ? 0 : reader.GetInt32(0);
                summary.NoResultSearches = reader.IsDBNull(1) ? 0 : reader.GetInt32(1);
                summary.UniqueUsers = reader.IsDBNull(2) ? 0 : reader.GetInt32(2);
            }
        }

        var clicksCmd = connection.CreateCommand();
        clicksCmd.CommandText = "SELECT COUNT(1) FROM PlanClickEvents WHERE CreatedAtUtc >= $since";
        clicksCmd.Parameters.AddWithValue("$since", since);
        summary.PlanClickEvents = Convert.ToInt32(await clicksCmd.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);

        var topCmd = connection.CreateCommand();
        topCmd.CommandText = """
            SELECT QueryText, COUNT(1) AS Cnt
            FROM SearchAnalytics
            WHERE CreatedAtUtc >= $since
            GROUP BY QueryText
            ORDER BY Cnt DESC, QueryText ASC
            LIMIT 10;
        """;
        topCmd.Parameters.AddWithValue("$since", since);
        await using var topReader = await topCmd.ExecuteReaderAsync(cancellationToken);
        while (await topReader.ReadAsync(cancellationToken))
        {
            summary.TopSearches.Add(new SearchTermAggregate
            {
                QueryText = topReader.GetString(0),
                Count = topReader.GetInt32(1)
            });
        }

        return summary;
    }

    public async Task<AppUser> CreateUserAsync(string email, string passwordHash, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var now = DateTime.UtcNow;
        var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO Users (Email, PasswordHash, CreatedAtUtc)
            VALUES ($email, $hash, $now);
            SELECT last_insert_rowid();
        """;
        cmd.Parameters.AddWithValue("$email", email.Trim());
        cmd.Parameters.AddWithValue("$hash", passwordHash);
        cmd.Parameters.AddWithValue("$now", now.ToString("O", CultureInfo.InvariantCulture));

        var id = Convert.ToInt32(await cmd.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);

        // Create Basic subscription for new user
        var subCmd = connection.CreateCommand();
        subCmd.CommandText = """
            INSERT OR IGNORE INTO UserSubscriptions (UserId, TierName, StartedAtUtc, ExpiresAtUtc, IsActive)
            VALUES ($userId, 'Basic', $started, NULL, 1);
        """;
        subCmd.Parameters.AddWithValue("$userId", id);
        subCmd.Parameters.AddWithValue("$started", now.ToString("O", CultureInfo.InvariantCulture));
        await subCmd.ExecuteNonQueryAsync(cancellationToken);

        return new AppUser { Id = id, Email = email.Trim(), PasswordHash = passwordHash, CreatedAtUtc = now };
    }

        public async Task<string?> GetStripeCustomerIdAsync(int userId, CancellationToken cancellationToken = default)
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT StripeCustomerId FROM Users WHERE Id = $userId LIMIT 1";
            cmd.Parameters.AddWithValue("$userId", userId);
            var result = await cmd.ExecuteScalarAsync(cancellationToken);
            return result is DBNull or null ? null : (string)result;
        }

        public async Task SetStripeCustomerIdAsync(int userId, string stripeCustomerId, CancellationToken cancellationToken = default)
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            var cmd = connection.CreateCommand();
            cmd.CommandText = "UPDATE Users SET StripeCustomerId = $cid WHERE Id = $userId";
            cmd.Parameters.AddWithValue("$cid", stripeCustomerId);
            cmd.Parameters.AddWithValue("$userId", userId);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        public async Task ActivateSubscriptionFromStripeAsync(string stripeCustomerId, string tierName, CancellationToken cancellationToken = default)
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            // Find user by Stripe customer ID
            var lookupCmd = connection.CreateCommand();
            lookupCmd.CommandText = "SELECT Id FROM Users WHERE StripeCustomerId = $cid LIMIT 1";
            lookupCmd.Parameters.AddWithValue("$cid", stripeCustomerId);
            var result = await lookupCmd.ExecuteScalarAsync(cancellationToken);
            if (result is null or DBNull) return;

            var userId = Convert.ToInt32(result, CultureInfo.InvariantCulture);
            var now = DateTime.UtcNow;
            var expires = now.AddDays(32); // monthly billing, 2-day grace

            var cmd = connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO UserSubscriptions (UserId, TierName, StartedAtUtc, ExpiresAtUtc, IsActive)
                VALUES ($userId, $tier, $started, $expires, 1)
                ON CONFLICT(UserId) DO UPDATE SET
                    TierName = excluded.TierName,
                    StartedAtUtc = excluded.StartedAtUtc,
                    ExpiresAtUtc = excluded.ExpiresAtUtc,
                    IsActive = 1;
            """;
            cmd.Parameters.AddWithValue("$userId", userId);
            cmd.Parameters.AddWithValue("$tier", tierName);
            cmd.Parameters.AddWithValue("$started", now.ToString("O", CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("$expires", expires.ToString("O", CultureInfo.InvariantCulture));
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

    public async Task<UserProfile?> GetUserProfileAsync(int userId, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT u.Id, u.Email, u.CreatedAtUtc, 
                   COALESCE(s.TierName, 'Basic') as TierName, s.ExpiresAtUtc, s.IsActive
            FROM Users u
            LEFT JOIN UserSubscriptions s ON u.Id = s.UserId
            WHERE u.Id = $userId LIMIT 1
        """;
        cmd.Parameters.AddWithValue("$userId", userId);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;

        var tier = reader.GetString(3);
        var expiresAtStr = reader.IsDBNull(4) ? null : reader.GetString(4);
        var isActive = reader.GetInt32(5) == 1;

        return new UserProfile
        {
            Id = reader.GetInt32(0),
            Email = reader.GetString(1),
            CreatedAtUtc = DateTime.Parse(reader.GetString(2), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            SubscriptionTier = tier,
            SubscriptionExpiresAtUtc = expiresAtStr != null ? DateTime.Parse(expiresAtStr, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) : null,
            IsSubscriptionActive = isActive && (expiresAtStr == null || DateTime.Parse(expiresAtStr, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) > DateTime.UtcNow)
        };
    }

    public async Task UpdateUserPasswordAsync(int userId, string newPasswordHash, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE Users SET PasswordHash = $hash WHERE Id = $userId";
        cmd.Parameters.AddWithValue("$hash", newPasswordHash);
        cmd.Parameters.AddWithValue("$userId", userId);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task DeleteUserAccountAsync(int userId, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);

        var deleteSearchAnalytics = connection.CreateCommand();
        deleteSearchAnalytics.Transaction = (SqliteTransaction)tx;
        deleteSearchAnalytics.CommandText = "DELETE FROM SearchAnalytics WHERE UserId = $userId";
        deleteSearchAnalytics.Parameters.AddWithValue("$userId", userId);
        await deleteSearchAnalytics.ExecuteNonQueryAsync(cancellationToken);

        var deletePlanClickEvents = connection.CreateCommand();
        deletePlanClickEvents.Transaction = (SqliteTransaction)tx;
        deletePlanClickEvents.CommandText = "DELETE FROM PlanClickEvents WHERE UserId = $userId";
        deletePlanClickEvents.Parameters.AddWithValue("$userId", userId);
        await deletePlanClickEvents.ExecuteNonQueryAsync(cancellationToken);

        var deleteResetTokens = connection.CreateCommand();
        deleteResetTokens.Transaction = (SqliteTransaction)tx;
        deleteResetTokens.CommandText = "DELETE FROM PasswordResetTokens WHERE UserId = $userId";
        deleteResetTokens.Parameters.AddWithValue("$userId", userId);
        await deleteResetTokens.ExecuteNonQueryAsync(cancellationToken);

        var deleteSubscription = connection.CreateCommand();
        deleteSubscription.Transaction = (SqliteTransaction)tx;
        deleteSubscription.CommandText = "DELETE FROM UserSubscriptions WHERE UserId = $userId";
        deleteSubscription.Parameters.AddWithValue("$userId", userId);
        await deleteSubscription.ExecuteNonQueryAsync(cancellationToken);

        var deleteUser = connection.CreateCommand();
        deleteUser.Transaction = (SqliteTransaction)tx;
        deleteUser.CommandText = "DELETE FROM Users WHERE Id = $userId";
        deleteUser.Parameters.AddWithValue("$userId", userId);
        await deleteUser.ExecuteNonQueryAsync(cancellationToken);

        await tx.CommitAsync(cancellationToken);
    }

    public async Task UpgradeSubscriptionAsync(int userId, string tierName, int daysValid = 30, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var now = DateTime.UtcNow;
        var expiresAt = now.AddDays(daysValid);

        var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO UserSubscriptions (UserId, TierName, StartedAtUtc, ExpiresAtUtc, IsActive)
            VALUES ($userId, $tier, $started, $expires, 1)
            ON CONFLICT(UserId) DO UPDATE SET
                TierName = excluded.TierName,
                ExpiresAtUtc = excluded.ExpiresAtUtc,
                IsActive = excluded.IsActive;
        """;
        cmd.Parameters.AddWithValue("$userId", userId);
        cmd.Parameters.AddWithValue("$tier", tierName);
        cmd.Parameters.AddWithValue("$started", now.ToString("O", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$expires", expiresAt.ToString("O", CultureInfo.InvariantCulture));
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

        public async Task CancelSubscriptionAsync_ByStripeCustomer(string stripeCustomerId, CancellationToken cancellationToken = default)
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            var lookupCmd = connection.CreateCommand();
            lookupCmd.CommandText = "SELECT Id FROM Users WHERE StripeCustomerId = $cid LIMIT 1";
            lookupCmd.Parameters.AddWithValue("$cid", stripeCustomerId);
            var result = await lookupCmd.ExecuteScalarAsync(cancellationToken);
            if (result is null or DBNull) return;

            var userId = Convert.ToInt32(result, CultureInfo.InvariantCulture);
            await CancelSubscriptionAsync(userId, cancellationToken);
        }

    public async Task CancelSubscriptionAsync(int userId, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var cmd = connection.CreateCommand();
        cmd.CommandText = """
            UPDATE UserSubscriptions
            SET TierName = 'None', IsActive = 0
            WHERE UserId = $userId;
        """;
        cmd.Parameters.AddWithValue("$userId", userId);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<HardPartIntelItem>> GetHardPartIntelAsync(int count = 12, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT Id, PartName, DeviceFamily, PainReason, SearchHint, ForumSignalSource, PainScore, IsScamSensitive
            FROM HardPartIntel
            ORDER BY PainScore DESC, PartName ASC
            LIMIT $count;
        """;
        cmd.Parameters.AddWithValue("$count", count);

        var items = new List<HardPartIntelItem>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new HardPartIntelItem
            {
                Id = reader.GetInt32(0),
                PartName = reader.GetString(1),
                DeviceFamily = reader.GetString(2),
                PainReason = reader.GetString(3),
                SearchHint = reader.GetString(4),
                ForumSignalSource = reader.GetString(5),
                PainScore = reader.GetInt32(6),
                IsScamSensitive = reader.GetInt32(7) == 1
            });
        }

        return items;
    }

    private static string LikeFilter(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "%";
        }

        return $"%{value.Trim()}%";
    }

    private static string HashToken(string token)
    {
        var bytes = Encoding.UTF8.GetBytes(token.Trim());
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }

    private static List<VendorPartOffer> BuildSeedOffers(DateTime now)
    {
        // Image URLs: real photos from Wikimedia Commons (CC-licensed, stable redirects)
        // Dell XPS 15 battery images: actual Dell Li-Ion battery product photos
        const string dellBattImg1 = "https://commons.wikimedia.org/w/index.php?title=Special:Redirect/file/Dell_Li-Ion_Battery_YRDD6-1339.jpg&width=800";
        const string dellBattImg2 = "https://commons.wikimedia.org/w/index.php?title=Special:Redirect/file/Dell_Battery_Module_X284G.jpg&width=800";
        const string dellBattImg3 = "https://commons.wikimedia.org/w/index.php?title=Special:Redirect/file/Dell_Laptop_Secondary_Battery_with_charge_level_indicator.jpg&width=800";

        // ASUS ROG STRIX B550-F motherboard images: real ASUS ROG/TUF gaming AM4 motherboard photos
        const string asusMbImg1 = "https://commons.wikimedia.org/w/index.php?title=Special:Redirect/file/Asus_ROG_Strix_Z390-H_motherboard.JPG&width=800";
        const string asusMbImg2 = "https://commons.wikimedia.org/w/index.php?title=Special:Redirect/file/ASUS_PRIME_X570-P_and_X570-PRO_20190601.jpg&width=800";
        const string asusMbImg3 = "https://commons.wikimedia.org/w/index.php?title=Special:Redirect/file/ASUS_TUF_B450-PLUS_GAMING_20181221.jpg&width=800";

        // Lenovo ThinkPad X1 Carbon keyboard images: actual ThinkPad X1 Carbon keyboard FRU photos
        const string lenovoKbImg1 = "https://commons.wikimedia.org/w/index.php?title=Special:Redirect/file/ThinkPad_X1_Carbon_Japanese_Keyboard.jpg&width=800";
        const string lenovoKbImg2 = "https://commons.wikimedia.org/w/index.php?title=Special:Redirect/file/Back_of_Lenovo_ThinkPad_X220_keyboard_FRU_45N2171.jpg&width=800";
        const string lenovoKbImg3 = "https://commons.wikimedia.org/w/index.php?title=Special:Redirect/file/Keyboard_Lenovo_ThinkPad_T440p_(QWERTY_layout).jpg&width=800";

        // HP EliteBook 840 G8 battery images: actual HP laptop battery product photos
        const string hpBattImg1 = "https://commons.wikimedia.org/w/index.php?title=Special:Redirect/file/HP_Laptop_Battery.jpg&width=800";
        const string hpBattImg2 = "https://commons.wikimedia.org/w/index.php?title=Special:Redirect/file/HP_EliteBook_Revolve_810_(9094538112).jpg&width=800";
        const string hpBattImg3 = "https://commons.wikimedia.org/w/index.php?title=Special:Redirect/file/HP_Laptop_PC_with_battery.jpg&width=800";

        var offers =
        new List<VendorPartOffer>
        {
            new() { Title = "Dell XPS 15 9570 Battery D449201", Brand = "Dell", Model = "XPS 15", PartType = "Battery", BasePartNumber = "D449201", Marketplace = "Amazon", Price = 94.99m, Currency = "USD", ListingUrl = "", ImageUrl1 = dellBattImg1, ImageUrl2 = dellBattImg2, ImageUrl3 = dellBattImg3, IsGenuineSupplier = true, SourceVerifiedAtUtc = now },
            new() { Title = "Dell XPS 15 9570 Battery D449201", Brand = "Dell", Model = "XPS 15", PartType = "Battery", BasePartNumber = "D449201", Marketplace = "eBay", Price = 95.49m, Currency = "USD", ListingUrl = "", ImageUrl1 = dellBattImg1, ImageUrl2 = dellBattImg2, ImageUrl3 = dellBattImg3, IsGenuineSupplier = true, SourceVerifiedAtUtc = now },
            new() { Title = "Dell XPS 15 9570 Battery D449201", Brand = "Dell", Model = "XPS 15", PartType = "Battery", BasePartNumber = "D449201", Marketplace = "Newegg", Price = 96.25m, Currency = "USD", ListingUrl = "", ImageUrl1 = dellBattImg1, ImageUrl2 = dellBattImg2, ImageUrl3 = dellBattImg3, IsGenuineSupplier = true, SourceVerifiedAtUtc = now },
            new() { Title = "Dell XPS 15 9570 Battery D449201", Brand = "Dell", Model = "XPS 15", PartType = "Battery", BasePartNumber = "D449201", Marketplace = "TigerDirect", Price = 95.99m, Currency = "USD", ListingUrl = "", ImageUrl1 = dellBattImg1, ImageUrl2 = dellBattImg2, ImageUrl3 = dellBattImg3, IsGenuineSupplier = true, SourceVerifiedAtUtc = now },
            new() { Title = "Dell XPS 15 9570 Battery D449201", Brand = "Dell", Model = "XPS 15", PartType = "Battery", BasePartNumber = "D449201", Marketplace = "MicroCenter", Price = 95.79m, Currency = "USD", ListingUrl = "", ImageUrl1 = dellBattImg1, ImageUrl2 = dellBattImg2, ImageUrl3 = dellBattImg3, IsGenuineSupplier = true, SourceVerifiedAtUtc = now },
            new() { Title = "ASUS ROG STRIX B550-F Motherboard A550FMB", Brand = "ASUS", Model = "B550-F", PartType = "Motherboard", BasePartNumber = "A550FMB", Marketplace = "Amazon", Price = 194.99m, Currency = "USD", ListingUrl = "", ImageUrl1 = asusMbImg1, ImageUrl2 = asusMbImg2, ImageUrl3 = asusMbImg3, IsGenuineSupplier = true, SourceVerifiedAtUtc = now },
            new() { Title = "ASUS ROG STRIX B550-F Motherboard A550FMB", Brand = "ASUS", Model = "B550-F", PartType = "Motherboard", BasePartNumber = "A550FMB", Marketplace = "eBay", Price = 193.49m, Currency = "USD", ListingUrl = "", ImageUrl1 = asusMbImg1, ImageUrl2 = asusMbImg2, ImageUrl3 = asusMbImg3, IsGenuineSupplier = true, SourceVerifiedAtUtc = now },
            new() { Title = "ASUS ROG STRIX B550-F Motherboard A550FMB", Brand = "ASUS", Model = "B550-F", PartType = "Motherboard", BasePartNumber = "A550FMB", Marketplace = "Newegg", Price = 195.75m, Currency = "USD", ListingUrl = "", ImageUrl1 = asusMbImg1, ImageUrl2 = asusMbImg2, ImageUrl3 = asusMbImg3, IsGenuineSupplier = true, SourceVerifiedAtUtc = now },
            new() { Title = "ASUS ROG STRIX B550-F Motherboard A550FMB", Brand = "ASUS", Model = "B550-F", PartType = "Motherboard", BasePartNumber = "A550FMB", Marketplace = "TigerDirect", Price = 196.25m, Currency = "USD", ListingUrl = "", ImageUrl1 = asusMbImg1, ImageUrl2 = asusMbImg2, ImageUrl3 = asusMbImg3, IsGenuineSupplier = true, SourceVerifiedAtUtc = now },
            new() { Title = "ASUS ROG STRIX B550-F Motherboard A550FMB", Brand = "ASUS", Model = "B550-F", PartType = "Motherboard", BasePartNumber = "A550FMB", Marketplace = "MicroCenter", Price = 195.95m, Currency = "USD", ListingUrl = "", ImageUrl1 = asusMbImg1, ImageUrl2 = asusMbImg2, ImageUrl3 = asusMbImg3, IsGenuineSupplier = true, SourceVerifiedAtUtc = now },
            new() { Title = "Lenovo ThinkPad X1 Carbon Keyboard LTPX1KB", Brand = "Lenovo", Model = "ThinkPad X1", PartType = "Keyboard", BasePartNumber = "LTPX1KB", Marketplace = "Amazon", Price = 61.99m, Currency = "USD", ListingUrl = "", ImageUrl1 = lenovoKbImg1, ImageUrl2 = lenovoKbImg2, ImageUrl3 = lenovoKbImg3, IsGenuineSupplier = true, SourceVerifiedAtUtc = now },
            new() { Title = "Lenovo ThinkPad X1 Carbon Keyboard LTPX1KB", Brand = "Lenovo", Model = "ThinkPad X1", PartType = "Keyboard", BasePartNumber = "LTPX1KB", Marketplace = "eBay", Price = 60.49m, Currency = "USD", ListingUrl = "", ImageUrl1 = lenovoKbImg1, ImageUrl2 = lenovoKbImg2, ImageUrl3 = lenovoKbImg3, IsGenuineSupplier = true, SourceVerifiedAtUtc = now },
            new() { Title = "Lenovo ThinkPad X1 Carbon Keyboard LTPX1KB", Brand = "Lenovo", Model = "ThinkPad X1", PartType = "Keyboard", BasePartNumber = "LTPX1KB", Marketplace = "Newegg", Price = 62.25m, Currency = "USD", ListingUrl = "", ImageUrl1 = lenovoKbImg1, ImageUrl2 = lenovoKbImg2, ImageUrl3 = lenovoKbImg3, IsGenuineSupplier = true, SourceVerifiedAtUtc = now },
            new() { Title = "Lenovo ThinkPad X1 Carbon Keyboard LTPX1KB", Brand = "Lenovo", Model = "ThinkPad X1", PartType = "Keyboard", BasePartNumber = "LTPX1KB", Marketplace = "TigerDirect", Price = 61.75m, Currency = "USD", ListingUrl = "", ImageUrl1 = lenovoKbImg1, ImageUrl2 = lenovoKbImg2, ImageUrl3 = lenovoKbImg3, IsGenuineSupplier = true, SourceVerifiedAtUtc = now },
            new() { Title = "Lenovo ThinkPad X1 Carbon Keyboard LTPX1KB", Brand = "Lenovo", Model = "ThinkPad X1", PartType = "Keyboard", BasePartNumber = "LTPX1KB", Marketplace = "MicroCenter", Price = 61.69m, Currency = "USD", ListingUrl = "", ImageUrl1 = lenovoKbImg1, ImageUrl2 = lenovoKbImg2, ImageUrl3 = lenovoKbImg3, IsGenuineSupplier = true, SourceVerifiedAtUtc = now },
            new() { Title = "HP EliteBook 840 G8 Battery H840G8", Brand = "HP", Model = "840 G8", PartType = "Battery", BasePartNumber = "H840G8", Marketplace = "Amazon", Price = 89.25m, Currency = "USD", ListingUrl = "", ImageUrl1 = hpBattImg1, ImageUrl2 = hpBattImg2, ImageUrl3 = hpBattImg3, IsGenuineSupplier = true, SourceVerifiedAtUtc = now },
            new() { Title = "HP EliteBook 840 G8 Battery H840G8", Brand = "HP", Model = "840 G8", PartType = "Battery", BasePartNumber = "H840G8", Marketplace = "eBay", Price = 88.75m, Currency = "USD", ListingUrl = "", ImageUrl1 = hpBattImg1, ImageUrl2 = hpBattImg2, ImageUrl3 = hpBattImg3, IsGenuineSupplier = true, SourceVerifiedAtUtc = now },
            new() { Title = "HP EliteBook 840 G8 Battery H840G8", Brand = "HP", Model = "840 G8", PartType = "Battery", BasePartNumber = "H840G8", Marketplace = "Newegg", Price = 88.99m, Currency = "USD", ListingUrl = "", ImageUrl1 = hpBattImg1, ImageUrl2 = hpBattImg2, ImageUrl3 = hpBattImg3, IsGenuineSupplier = true, SourceVerifiedAtUtc = now },
            new() { Title = "HP EliteBook 840 G8 Battery H840G8", Brand = "HP", Model = "840 G8", PartType = "Battery", BasePartNumber = "H840G8", Marketplace = "TigerDirect", Price = 89.49m, Currency = "USD", ListingUrl = "", ImageUrl1 = hpBattImg1, ImageUrl2 = hpBattImg2, ImageUrl3 = hpBattImg3, IsGenuineSupplier = true, SourceVerifiedAtUtc = now },
            new() { Title = "HP EliteBook 840 G8 Battery H840G8", Brand = "HP", Model = "840 G8", PartType = "Battery", BasePartNumber = "H840G8", Marketplace = "MicroCenter", Price = 89.19m, Currency = "USD", ListingUrl = "", ImageUrl1 = hpBattImg1, ImageUrl2 = hpBattImg2, ImageUrl3 = hpBattImg3, IsGenuineSupplier = true, SourceVerifiedAtUtc = now }
        };

        foreach (var offer in offers)
        {
            offer.SourceType = "Seed";
        }

        return offers;
    }
}
