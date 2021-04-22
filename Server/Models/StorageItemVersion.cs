﻿using System;
using System.Collections.Generic;

namespace ThriveDevCenter.Server.Models
{
    using System.Threading.Tasks;
    using Microsoft.EntityFrameworkCore;

    [Index(nameof(StorageFileId))]
    [Index(new[] { nameof(StorageItemId), nameof(Version) }, IsUnique = true)]
    public class StorageItemVersion : UpdateableModel
    {
        public int Version { get; set; } = 1;

        public long StorageItemId { get; set; }
        public StorageItem StorageItem { get; set; }

        public long StorageFileId { get; set; }
        public StorageFile StorageFile { get; set; }

        public bool Keep { get; set; } = false;
        public bool Protected { get; set; } = false;
        public bool Uploading { get; set; } = true;

        public async Task<string> ComputeStoragePath(ApplicationDbContext database)
        {
            string parentPath = string.Empty;

            if (StorageItem.ParentId != null)
            {
                var parent = StorageItem.Parent;

                if (parent == null)
                {
                    parent = await database.StorageItems.FindAsync(StorageItem.ParentId);

                    if (parent == null)
                        throw new NullReferenceException("failed to get the StorageItem parent for path calculation");
                }

                parentPath = await parent.ComputeStoragePath(database) + '/';
            }

            return $"{parentPath}{Version}/{StorageItem.Name}";
        }

        public async Task<StorageFile> CreateStorageFile(ApplicationDbContext database, DateTime uploadExpiresAt, int size)
        {
            var file = new StorageFile()
            {
                StoragePath = await ComputeStoragePath(database),
                Size = size,
                Uploading = true,
                UploadExpires = uploadExpiresAt + TimeSpan.FromSeconds(1)
            };

            StorageFile = file;

            await database.StorageFiles.AddAsync(file);
            return file;
        }
    }
}
