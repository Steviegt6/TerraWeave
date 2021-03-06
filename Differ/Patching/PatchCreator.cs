﻿using Mono.Cecil;
using Mono.Cecil.Cil;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Terraweave.Common;
using Terraweave.Common.Data;
using Terraweave.Common.Patching;

namespace Terraweave.Differ.Patching
{
	public static class PatchCreator
	{
		public static void Start(string workingDirectory)
		{
			if (!File.Exists(Path.Combine(workingDirectory, "Terraria.exe")))
				Program.Panic("Could not find the vanilla Terraria.exe");

			if (!File.Exists(Path.Combine(workingDirectory, "TerrariaModified.exe")))
				Program.Panic("Could not find the modded Terraria.exe (must be named TerrariaModified.exe)");

			ModuleUtils.Initialize(workingDirectory);

			int patchCount = 0;

			TypeDefinition[] injectedTypes = ModuleUtils.ModdedModule.GetTypes()
				.Where(type =>
				!type.Name.Contains("<")
				&& ModuleUtils.TerrariaModule.GetType(type.FullName) == null)
				.ToArray();

			TypeDefinition[] injectedNestedTypes = injectedTypes
				.Where(type =>
				type.IsNested
				&& ModuleUtils.TerrariaModule.GetType(type.DeclaringType.FullName) != null)
				.ToArray();

			injectedTypes = injectedTypes.Where(type => !type.IsNested).ToArray();

			Log("Scanning for modified methods. This will take a while...");

			MethodDefinition[] vanillaMethods = ModuleUtils.TerrariaModule.Types
				.SelectMany(type => type.Methods)
				.ToArray();

			MethodDefinition[] moddedMethods = ModuleUtils.ModdedModule.Types
				.SelectMany(type => type.Methods)
				.ToArray();

			List<MethodModifyPatch> methodModifyPatches = new List<MethodModifyPatch>();

			foreach (MethodDefinition moddedMethod in moddedMethods)
			{
				MethodDefinition vanillaMethod = vanillaMethods.Where(method => moddedMethod.FullName == method.FullName)
					.FirstOrDefault();

				if (vanillaMethod == null)
					continue;

				Instruction[] vanillaInstructions = vanillaMethod.Body.Instructions.ToArray();
				Instruction[] moddedInstructions = moddedMethod.Body.Instructions.ToArray();

				List<MethodChange> changes = new List<MethodChange>();

				for (int i = 0; i < moddedInstructions.Length; i++)
				{
					if (vanillaInstructions[i] != moddedInstructions[i])
					{
						if (vanillaInstructions[i] == moddedInstructions[i + 1])
							changes.Add(new MethodChange(InstructionAction.Insert, moddedInstructions[i], i));
						else if (vanillaInstructions[i].OpCode == moddedInstructions[i].OpCode)
							changes.Add(new MethodChange(InstructionAction.Modify, moddedInstructions[i], i));
						else
							changes.Add(new MethodChange(InstructionAction.Remove, null, i));

						i++;
						continue;
					}
				}

				if (changes.Count != 0)
					methodModifyPatches.Add(new MethodModifyPatch(vanillaMethod, changes.ToArray()));
			}

			patchCount += injectedTypes.Length + injectedNestedTypes.Length + methodModifyPatches.Count;

			using (MemoryStream stream = new MemoryStream())
			{
				using (BinaryWriter writer = new BinaryWriter(stream))
				{
					writer.Write(System.Text.Encoding.ASCII.GetBytes("TWEAVE"));

					writer.Write(patchCount);

					foreach (TypeDefinition type in injectedTypes)
					{
						Log($"Detected type to inject: {type.FullName}");
						writer.Write(PatchTypes.TypeInject);
						new TypeInjectPatch(type).SerializePatch(writer);
					}

					foreach (TypeDefinition type in injectedNestedTypes)
					{
						Log($"Detected nested type to inject: {type.FullName}");
						writer.Write(PatchTypes.NestedTypeInject);
						new NestedTypeInjectPatch(type.DeclaringType, type).SerializePatch(writer);
					}

					foreach (MethodModifyPatch patch in methodModifyPatches)
					{
						Log($"Detected modified method: {patch.Method}");
						writer.Write(PatchTypes.MethodModify);
						patch.SerializePatch(writer);
					}
				}

				File.WriteAllBytes("patch.tweave", stream.ToArray());
			}

			TestDeserializePatches();
		}

		// this will be moved to installer eventually, just here for testing
		public static void TestDeserializePatches()
		{
			Patch[] patches;

			using (BinaryReader reader = new BinaryReader(File.Open("patch.tweave", FileMode.Open)))
			{
				if (System.Text.Encoding.ASCII.GetString(reader.ReadBytes(6)) != "TWEAVE")
					Program.Panic("Not a tweave file!");

				int patchCount = reader.ReadInt32();
				patches = new Patch[patchCount];

				for (int i = 0; i < patchCount; i++)
				{
					byte patchType = reader.ReadByte();

					switch (patchType)
					{
						case PatchTypes.TypeInject:
							patches[i] = new TypeInjectPatch(reader);
							break;

						case PatchTypes.NestedTypeInject:
							patches[i] = new NestedTypeInjectPatch(reader);
							break;

						case PatchTypes.MethodModify:
							patches[i] = new MethodModifyPatch(reader);
							break;
					}
				}
			}

			foreach (Patch patch in patches)
				patch.Apply(ModuleUtils.TerrariaModule);

			ModuleUtils.TerrariaModule.Write("PatchedTerraria.exe");
		}

		public static void Log(string message) => Console.WriteLine(message);
	}
}