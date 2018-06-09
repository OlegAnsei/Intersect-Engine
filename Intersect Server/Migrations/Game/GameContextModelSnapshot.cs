﻿// <auto-generated />
using Intersect.Server.Classes.Database.GameData;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Storage.Internal;
using System;

namespace Intersect.Server.Migrations.Game
{
    [DbContext(typeof(GameContext))]
    partial class GameContextModelSnapshot : ModelSnapshot
    {
        protected override void BuildModel(ModelBuilder modelBuilder)
        {
#pragma warning disable 612, 618
            modelBuilder
                .HasAnnotation("ProductVersion", "2.0.1-rtm-125");

            modelBuilder.Entity("Intersect.GameObjects.AnimationBase", b =>
                {
                    b.Property<Guid>("Id")
                        .ValueGeneratedOnAdd();

                    b.Property<bool>("DisableLowerRotations");

                    b.Property<bool>("DisableUpperRotations");

                    b.Property<int>("LowerAnimFrameCount");

                    b.Property<int>("LowerAnimFrameSpeed");

                    b.Property<int>("LowerAnimLoopCount");

                    b.Property<string>("LowerAnimSprite");

                    b.Property<int>("LowerAnimXFrames");

                    b.Property<int>("LowerAnimYFrames");

                    b.Property<string>("Name");

                    b.Property<string>("Sound");

                    b.Property<int>("UpperAnimFrameCount");

                    b.Property<int>("UpperAnimFrameSpeed");

                    b.Property<int>("UpperAnimLoopCount");

                    b.Property<string>("UpperAnimSprite");

                    b.Property<int>("UpperAnimXFrames");

                    b.Property<int>("UpperAnimYFrames");

                    b.HasKey("Id");

                    b.ToTable("Animations");
                });
#pragma warning restore 612, 618
        }
    }
}
