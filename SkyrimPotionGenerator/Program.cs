using DynamicData.Kernel;
using Mutagen.Bethesda;
using Mutagen.Bethesda.FormKeys.SkyrimSE;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Mutagen.Bethesda.Strings;
using Mutagen.Bethesda.Synthesis;
using Noggog;
using System.Collections.Immutable;

namespace SkyrimPotionGenerator
{
    public class Program
    {
        public static readonly Ingestible.TranslationMask PotionCopyMask = new(true)
        {
            EditorID = false,
            Effects = false,
            Name = false,
            Description = false
        };

        public static readonly HashSet<IFormLinkGetter<IMagicEffectGetter>> AlchRestoreHealthSet = new() { Skyrim.MagicEffect.AlchRestoreHealth };

        public static async Task<int> Main(string[] args)
        {
            return await SynthesisPipeline.Instance
                .AddPatch<ISkyrimMod, ISkyrimModGetter>(RunPatch)
                .SetTypicalOpen(GameRelease.SkyrimSE, "YourPatcher.esp")
                .Run(args);
        }

        public static void RunPatch(IPatcherState<ISkyrimMod, ISkyrimModGetter> state)
        {

            var alchemyPerks = FindConditionalAlchemyPerks(state);

            var ingredients = state.LoadOrder.PriorityOrder.Ingredient().WinningOverrides().ToList();

            var bestIngredientByEffect = IndexIngredientsByBestEffect(ingredients);

            var standardPotions = IndexStandardPotions(state);

            foreach (var ingredient1 in ingredients)
            {
                foreach (var ingredient2 in ingredients)
                {
                    if (ingredient2 == ingredient1) break;

                    MakePotions(ingredient1.Effects
                        .Concat(ingredient2.Effects)
                        .GroupBy(i => i.BaseEffect)
                        .Where(i => i.Count() > 1)
                        .AsList(),
                        state,
                        alchemyPerks,
                        bestIngredientByEffect,
                        standardPotions);

                    foreach (var ingredient3 in ingredients)
                    {
                        if (ingredient3 == ingredient2) break;

                        MakePotions(ingredient1.Effects
                            .Concat(ingredient2.Effects)
                            .Concat(ingredient3.Effects)
                            .GroupBy(i => i.BaseEffect)
                            .Where(i => i.Count() > 1)
                            .AsList(),
                            state,
                            alchemyPerks,
                            bestIngredientByEffect,
                            standardPotions);
                    }
                }
            }

        }

        private static Dictionary<HashSet<IFormLinkGetter<IMagicEffectGetter>>, List<IIngestibleGetter>> IndexStandardPotions(IPatcherState<ISkyrimMod, ISkyrimModGetter> state) => state.LoadOrder.PriorityOrder.Ingestible()
                .WinningOverrides()
                .Where(potion => potion.Keywords?.Contains(Skyrim.Keyword.VendorItemPotion) == true
                              || potion.Keywords?.Contains(Skyrim.Keyword.VendorItemPoison) == true)
                .OrderBy(potion => potion.Value)
                .GroupBy(
                    potion => (
                        from effect in potion.Effects
                        where effect.Data is not null && effect.Data.Magnitude != 0
                        let magicEffectLink = effect.BaseEffect
                        let magicEffect = magicEffectLink.TryResolve(state.LinkCache, out var theRecord) ? theRecord : null
                        where magicEffect?.Name?.String is not null
                        select (IFormLinkGetter<IMagicEffectGetter>)magicEffectLink
                     ).ToHashSet(),
                    HashSet<IFormLinkGetter<IMagicEffectGetter>>.CreateSetComparer()
                )
                .ToDictionary(
                    group => group.Key,
                    group => group.ToList(),
                    HashSet<IFormLinkGetter<IMagicEffectGetter>>.CreateSetComparer()
                );

        private static double PotionEffectCost(IEffectGetter effect)
        {
            var mag = effect.Data?.Magnitude ?? 0;
            var dur = effect.Data?.Duration ?? 0;
            if (mag < 0) mag = 1;
            if (dur == 0) dur = 10;
            return Math.Pow(mag * (dur / 10), 1.1);
        }

        private static Dictionary<IFormLinkNullableGetter<IMagicEffectGetter>, List<IEffectGetter>> IndexIngredientsByBestEffect(List<IIngredientGetter>? ingredients) => (
            from ingredient in ingredients
            from effect in ingredient.Effects
            orderby PotionEffectCost(effect) descending
            group effect by effect.BaseEffect
        )
        .ToDictionary(
            ingredientsByEffect => ingredientsByEffect.Key,
            ingredientsByEffect => ingredientsByEffect.ToList()
        );

        private static IList<IPerkGetter> FindConditionalAlchemyPerks(IPatcherState<ISkyrimMod, ISkyrimModGetter> state) => (
            from perk in state.LoadOrder.PriorityOrder.Perk().WinningOverrides()
            where (
                from effect in perk.Effects
                where effect is IAPerkEntryPointEffectGetter
                let entryPointEffect = effect as IAPerkEntryPointEffectGetter
                from entryPointCondition in entryPointEffect.Conditions
                from condition in entryPointCondition.Conditions
                where condition is IConditionFloatGetter
                let floatCondition = condition as IConditionFloatGetter
                where floatCondition.Data switch
                {
                    IEPAlchemyEffectHasKeywordConditionDataGetter => true,
                    IEPAlchemyGetMakingPoisonConditionDataGetter => true,
                    _ => false,
                }
                select true
            )
            .Any()
            select perk
        ).ToList();

        private static void MakePotions(
            IList<IGrouping<IFormLinkNullableGetter<IMagicEffectGetter>, IEffectGetter>> commonEffects,
            IPatcherState<ISkyrimMod, ISkyrimModGetter> state,
            IList<IPerkGetter> alchemyPerks,
            Dictionary<IFormLinkNullableGetter<IMagicEffectGetter>, List<IEffectGetter>> bestIngredientByEffect,
            Dictionary<HashSet<IFormLinkGetter<IMagicEffectGetter>>, List<IIngestibleGetter>> standardPotions)
        {
            if (!commonEffects.Any())
                return;

            var temp = new List<(float baseEffectCost, bool isPoison, IEffectGetter effect, IMagicEffectGetter magicEffect, IFormLinkGetter<IMagicEffectGetter> magicEffectLink)>();

            foreach (var effectSet in commonEffects)
            {
                var magicEffectLink = effectSet.Key;
                magicEffectLink.TryResolve(state.LinkCache, out var magicEffect);
                if (magicEffect is null)
                    return;

                var ingredientsWithThisEffect = bestIngredientByEffect[effectSet.Key];

                var effect = effectSet.OrderBy(e => ingredientsWithThisEffect.IndexOf(e)).First();

                if (effect.Data is null)
                    continue;

                var magnitude = effect.Data?.Magnitude ?? 0;
                if (magnitude < 1) magnitude = 1;

                if (magicEffect.Flags.HasFlag(MagicEffect.Flag.NoMagnitude))
                    magnitude = 0;
                magnitude = (float)Math.Round(magnitude);

                float duration = effect.Data?.Duration ?? 0;
                if (magicEffect.Flags.HasFlag(MagicEffect.Flag.NoDuration) || duration < 0)
                    duration = 0;
                duration = (float)Math.Round(duration);


                var magnitudeFactor = 1.0;
                if (magnitude > 0) magnitudeFactor = magnitude;
                var durationFactor = 1.0;
                if (duration > 0) durationFactor = duration / 10;

                var baseEffectCost = (float)Math.Floor(magicEffect.BaseCost * Math.Pow(magnitudeFactor * durationFactor, 1.1));

                temp.Add((baseEffectCost, magicEffect.Flags.HasFlag(MagicEffect.Flag.Detrimental), effect, magicEffect, magicEffectLink));
            }

            // slower, but intent is clearer than using Sort.
            temp = temp.OrderByDescending(i => i.baseEffectCost).ToList();

            var resultIsPoison = temp.First().isPoison;

            var isFirst = true;
            float finalMagnitude = 0;
            float finalDuration = 0;
            var magicEffectLinks = new List<IFormLinkGetter<IMagicEffectGetter>>();
            var magicEffects = new List<IMagicEffectGetter>();

            foreach (var item in temp)
            {
                // we are creating standard potions/poisons to be sold commercially, so we definitely want to apply:
                // pure mixtures - skip effects that are not the same kind as the best effect.
                if (item.isPoison != resultIsPoison)
                    continue;

                var effect = item.effect;
                var magicEffect = item.magicEffect;

                var powerFactor = PowerFactor(magicEffect, alchemyPerks, resultIsPoison);

                var magnitude = effect.Data?.Magnitude ?? 0;
                if (magnitude < 1)
                    magnitude = 1;

                if (magicEffect.Flags.HasFlag(MagicEffect.Flag.NoMagnitude))
                    magnitude = 0;
                var magnitudeFactor = 1.0;
                if (magicEffect.Flags.HasFlag(MagicEffect.Flag.PowerAffectsMagnitude))
                    magnitudeFactor = powerFactor;
                magnitude = (float)Math.Round(magnitude * magnitudeFactor);

                float duration = effect.Data?.Duration ?? 0;
                if (magicEffect.Flags.HasFlag(MagicEffect.Flag.NoDuration) || duration < 0)
                    duration = 0;
                var durationFactor = 1.0;
                if (magicEffect.Flags.HasFlag(MagicEffect.Flag.PowerAffectsDuration))
                    durationFactor = powerFactor;
                duration = (int)Math.Round(duration * durationFactor);

                //magnitudeFactor = 1.0;
                //if (magnitude > 0) magnitudeFactor = magnitude;
                //durationFactor = 1.0;
                //if (duration > 0) durationFactor = duration / 10;

                //var effectCost = (float)Math.Floor(magicEffect.BaseCost * Math.Pow(magnitudeFactor * durationFactor, 1.1));


                magicEffectLinks.Add(item.magicEffectLink);
                magicEffects.Add(magicEffect);

                if (isFirst)
                {
                    finalMagnitude = magnitude;
                    finalDuration = duration;
                    isFirst = false;
                }
            }

            // shouldn't happen: we found no valid magic effects
            if (isFirst)
                return;

            List<IIngestibleGetter>? standardPotionsList;

            if (magicEffectLinks.Contains(Skyrim.MagicEffect.AlchCureDisease))
            {
                // TODO special case for Cure Disease; magnitude makes no difference to the final effect, so scaling by cure disease doesn't make sense.
                if (standardPotions.TryGetValue(magicEffectLinks.ToHashSet(), out standardPotionsList))
                {
                    // this exact combination of effects already exists as a vanilla potion, we don't need to do anything.
                    return;
                }

                // the only effect is cure disease... but there's no vanilla potion? uh, not touching that.
                if (magicEffectLinks.Count == 1)
                    return;

                var tempLinks = magicEffectLinks.ToList();
                tempLinks.Remove(Skyrim.MagicEffect.AlchCureDisease);

                if (!(standardPotions.TryGetValue(tempLinks.ToHashSet(), out standardPotionsList)
                   || standardPotions.TryGetValue(tempLinks.Take(1).ToHashSet(), out standardPotionsList)
                   || standardPotions.TryGetValue(AlchRestoreHealthSet, out standardPotionsList)))
                    return;
            }
            else
            {
                if (standardPotions.TryGetValue(magicEffectLinks.ToHashSet(), out standardPotionsList))
                {
                    // this exact combination of effects already exists as a vanilla potion, we don't need to do anything.
                    return;
                }

                if (!(standardPotions.TryGetValue(magicEffectLinks.Take(1).ToHashSet(), out standardPotionsList)
                   || standardPotions.TryGetValue(AlchRestoreHealthSet, out standardPotionsList)))
                    return;
            }

            // we're creating new potions for this precise combination of effects... add this to the list of standard potions so we only do this once.
            standardPotions.Add(magicEffectLinks.ToHashSet(), new ());

            var targetLanguage = magicEffects
                .Select(magicEffect => magicEffect.Name?.TargetLanguage)
                .NotNull()
                .First();

            // TODO better support for translations.
            var newPotionName = (resultIsPoison ? "Poison" : "Potion") + " of " + FormatList(
                magicEffects
                    .Select(magicEffect => magicEffect.Name?.String)
                    .NotNull()
                    .ToList(),
                targetLanguage
            );

            Console.WriteLine("Adding new " + newPotionName);

            var baseEditorID = "spg_" + string.Join("_", magicEffectLinks.Select(link => link.FormKey.ToString()));

            foreach (var standardPotion in standardPotionsList)
            {
                var firstValidEffect = standardPotion.Effects
                    .First(effect => effect.Data is not null);

                var targetMagnitude = firstValidEffect.Data!.Magnitude;
                float scaleFactor = targetMagnitude / (float)finalMagnitude;

                var targetDuration = firstValidEffect.Data!.Duration * scaleFactor;

                var editorID = baseEditorID + "_" + targetMagnitude;

                var newPotion = state.PatchMod.Ingestibles.AddNew(editorID);

                newPotion.DeepCopyIn(standardPotion, copyMask: PotionCopyMask);

                newPotion.Name = newPotionName;

                var magnitudeFactor = targetMagnitude > 0 ? targetMagnitude : 1.0;
                var durationFactor = targetDuration > 0 ? targetDuration / 10 : 1.0;

                newPotion.Value = (uint)Math.Floor(
                    magicEffects
                    .Select(magicEffect => magicEffect.BaseCost * Math.Pow(magnitudeFactor * durationFactor, 1.1))
                    .Sum()
                );

                foreach (var magicEffectLink in magicEffectLinks)
                {
                    newPotion.Effects.Add(new()
                    {
                        BaseEffect = magicEffectLink.AsNullable(),
                        Data = new()
                        {
                            Magnitude = targetMagnitude,
                            Duration = (int)Math.Round(finalDuration * scaleFactor)
                        }
                    });
                }
            }
        }

        private static string FormatList(List<string> enumerable, Language targetLanguage) => targetLanguage switch
        {
            // TODO better support for translations.
            Language.English => enumerable.Count >= 3 ? string.Join(", ", enumerable.SkipLast(1)) + " and " + enumerable.Last()
                              : enumerable.Count >= 2 ? string.Join(" and ", enumerable)
                              : enumerable.First(),
            _ => string.Join(", ", enumerable),
        };

        private static float PowerFactor(IMagicEffectGetter magicEffect, IList<IPerkGetter> alchemyPerks, bool resultIsPoison)
        {
            var powerFactor = 1.0;

            if (magicEffect.Keywords is null || magicEffect.Keywords.Count == 0)
                return (float)powerFactor;

            foreach (var perk in alchemyPerks)
            {
                foreach (var effect1 in perk.Effects)
                {
                    if (effect1 is not IAPerkEntryPointEffectGetter effect2) continue;
                    if (effect2.EntryPoint != APerkEntryPointEffect.EntryType.ModAlchemyEffectiveness) continue;

                    foreach (var cond in effect2.Conditions)
                    {
                        foreach (var cond2 in cond.Conditions)
                        {
                            if (cond2 is not IConditionFloatGetter cond3) continue;
                            if (cond3.Data is IEPAlchemyEffectHasKeywordConditionDataGetter data)
                            {
                                if (magicEffect.Keywords.Contains(data.Keyword.Link))
                                {
                                    // TODO confirm that this applies when the keyword is present, and not when it is absent.
                                    // TODO read the actual amount from the effect data
                                    // TODO only apply if all conditions match.
                                    powerFactor *= 1.25;
                                }
                            }
                            else if (cond3.Data is IEPAlchemyGetMakingPoisonConditionDataGetter)
                            {
                                if (resultIsPoison)
                                {
                                    // TODO now what?
                                }
                                throw new NotImplementedException("TODO: Applies if the result is a poison. Which we don't know until the total effect is calculated, I think?");
                            }
                        }
                    }

                }
            }

            return (float)powerFactor;
        }
    }
}
