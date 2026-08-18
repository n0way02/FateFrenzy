using FateFrenzy.Core.External;
using FateFrenzy.Windows.Components;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Bindings.ImGui;
using System;
using System.Numerics;

namespace FateFrenzy.Windows;

public static class Tutorial
{
    private static int currentStep = 0;
    private const int MaxSteps = 5;

    public static void Draw(Plugin plugin)
    {
        var cfg = plugin.Configuration;
        var s = ImGuiHelpers.GlobalScale;

        // Header / Language Selector
        ImGui.Spacing();
        ImGui.TextColored(Styling.TextMuted, "Tutorial Language / Idioma do Tutorial:");
        ImGui.SameLine();

        using (ImRaii.PushStyle(ImGuiStyleVar.FramePadding, new Vector2(8 * s, 3 * s)))
        {
            var isEn = cfg.TutorialLanguage == "en";
            var isPt = cfg.TutorialLanguage == "pt";

            using (ImRaii.PushColor(ImGuiCol.Button, isEn ? Styling.AccentPink : Styling.CardBg))
            using (ImRaii.PushColor(ImGuiCol.ButtonHovered, Styling.AccentPink * 1.2f))
            {
                if (ImGui.Button("English"))
                {
                    cfg.TutorialLanguage = "en";
                    cfg.SaveDebounced();
                }
            }

            ImGui.SameLine();

            using (ImRaii.PushColor(ImGuiCol.Button, isPt ? Styling.AccentPink : Styling.CardBg))
            using (ImRaii.PushColor(ImGuiCol.ButtonHovered, Styling.AccentPink * 1.2f))
            {
                if (ImGui.Button("Português"))
                {
                    cfg.TutorialLanguage = "pt";
                    cfg.SaveDebounced();
                }
            }
        }

        ImGui.Separator();
        ImGui.Spacing();

        // Main Onboarding Card
        var contentSize = ImGui.GetContentRegionAvail();
        var cardH = Math.Max(320 * s, contentSize.Y - 60 * s);

        var dl = ImGui.GetWindowDrawList();
        var origin = ImGui.GetCursorScreenPos();
        var end = origin + new Vector2(contentSize.X, cardH);

        dl.AddRectFilled(origin, end, ImGui.GetColorU32(Styling.CardBg), Styling.CardRounding);
        dl.AddRect(origin, end, ImGui.GetColorU32(Styling.WithAlpha(Styling.AccentPink, 0.35f)), Styling.CardRounding, ImDrawFlags.None, 1f);

        // Draw card content
        ImGui.SetCursorScreenPos(origin + new Vector2(20 * s, 20 * s));
        var innerW = contentSize.X - 40 * s;

        using (ImRaii.PushColor(ImGuiCol.Text, Styling.TextStrong))
        {
            ImGui.PushTextWrapPos(origin.X + contentSize.X - 20 * s);
            DrawStepContent(cfg, innerW);
            ImGui.PopTextWrapPos();
        }

        // Draw Progress Indicators/Pills at the bottom of the card
        var pillY = end.Y - 30 * s;
        var dotR = 6f * s;
        var dotGap = 20f * s;
        var totalW = (MaxSteps - 1) * dotGap;
        var startX = origin.X + (contentSize.X - totalW) * 0.5f;

        for (int i = 0; i < MaxSteps; i++)
        {
            var dotC = new Vector2(startX + i * dotGap, pillY);
            var isCurrent = i == currentStep;
            var color = isCurrent ? Styling.AccentPink : Styling.TextMuted;
            dl.AddCircleFilled(dotC, dotR, ImGui.GetColorU32(color));
            if (isCurrent)
            {
                dl.AddCircle(dotC, dotR + 3f * s, ImGui.GetColorU32(Styling.WithAlpha(Styling.AccentPink, 0.45f)), 16, 1.5f * s);
            }
        }

        // Action Buttons below the card
        ImGui.SetCursorScreenPos(new Vector2(origin.X, origin.Y + cardH + 12 * s));

        using (ImRaii.PushStyle(ImGuiStyleVar.FramePadding, new Vector2(16 * s, 8 * s)))
        {
            // Left: Skip
            using (ImRaii.PushColor(ImGuiCol.Text, Styling.TextMuted))
            {
                if (ImGui.Button(GetText(cfg, "skip", "Skip Tutorial")))
                {
                    cfg.CompletedTutorial = true;
                    cfg.Save();
                }
            }

            ImGui.SameLine(ImGui.GetContentRegionAvail().X - 180 * s);

            // Right: Prev / Next / Finish
            if (currentStep > 0)
            {
                if (ImGui.Button(GetText(cfg, "prev", "Back")))
                {
                    currentStep--;
                }
                ImGui.SameLine();
            }

            var nextLabel = currentStep == MaxSteps - 1 ? GetText(cfg, "finish", "Finish & Start") : GetText(cfg, "next", "Next");
            using (ImRaii.PushColor(ImGuiCol.Button, Styling.AccentPink))
            using (ImRaii.PushColor(ImGuiCol.ButtonHovered, Styling.AccentPink * 1.15f))
            {
                if (ImGui.Button(nextLabel))
                {
                    if (currentStep == MaxSteps - 1)
                    {
                        cfg.CompletedTutorial = true;
                        cfg.Save();
                    }
                    else
                    {
                        currentStep++;
                    }
                }
            }
        }
    }

    private static void DrawHeader(string text)
    {
        using (ImRaii.PushColor(ImGuiCol.Text, Styling.TextStrong))
        {
            ImGui.SetWindowFontScale(1.25f);
            ImGui.TextUnformatted(text);
            ImGui.SetWindowFontScale(1f);
        }
    }

    private static void DrawStepContent(Configuration cfg, float width)
    {
        var isPt = cfg.TutorialLanguage == "pt";
        var s = ImGuiHelpers.GlobalScale;

        switch (currentStep)
        {
            case 0:
                DrawHeader(isPt ? "Bem-vindo ao FateFrenzy!" : "Welcome to FateFrenzy!");
                ImGui.Spacing();
                ImGui.Spacing();
                ImGui.TextUnformatted(isPt
                    ? "O FateFrenzy é o companheiro nativo definitivo para farm de FATEs.\n\nProjetado para ser extremamente rápido, leve e totalmente livre de crashes do Lua, este plugin ajudará você a farmar Gemas Bicolores e subir o nível das suas classes de forma totalmente automatizada.\n\nVamos fazer um tour rápido de 1 minuto para configurar tudo!"
                    : "FateFrenzy is the ultimate native FATE grinding companion.\n\nDesigned to be extremely fast, lightweight, and completely free of Lua crash issues, this plugin will help you farm Bicolor Gemstones and level up your classes with ease.\n\nLet's take a quick 1-minute tour to set everything up!");
                break;

            case 1:
                DrawHeader(isPt ? "Plugins Necessários" : "Required Plugins");
                ImGui.Spacing();
                ImGui.Spacing();
                ImGui.TextUnformatted(isPt
                    ? "Para que a automação funcione perfeitamente, o FateFrenzy se comunica com outros plugins do Dalamud. Certifique-se de ter instalado:\n\n" +
                      "• vnavmesh - Para navegação de mapa e movimentação 3D.\n" +
                      "• Lifestream - Para trocas de instâncias e viagens entre mundos.\n" +
                      "• RotationSolver ou BossMod - Para gerenciar suas habilidades de combate.\n\n" +
                      "Você pode checar o status de cada um deles abrindo a janela 'Dependencies'."
                    : "For the automation to work flawlessly, FateFrenzy integrates with other Dalamud plugins. Make sure you have installed:\n\n" +
                      "• vnavmesh - For pathfinding and 3D movement.\n" +
                      "• Lifestream - For switching instances and traveling between worlds.\n" +
                      "• RotationSolver or BossMod - For combat rotations.\n\n" +
                      "You can inspect the status of each requirement inside the 'Dependencies' tab.");
                break;

            case 2:
                DrawHeader(isPt ? "Seleção de Zonas" : "Zone Selection");
                ImGui.Spacing();
                ImGui.Spacing();
                ImGui.TextUnformatted(isPt
                    ? "Na aba principal do plugin, você escolhe as zonas que deseja farmar.\n\n" +
                      "• Multi-Zone: Se ativado, o plugin mudará de mapa automaticamente assim que as FATEs da zona atual acabarem.\n" +
                      "• World Rotation: Se ativo junto com a rotação, o plugin usará Lifestream para mudar para o próximo servidor da sua lista assim que passar por todas as zonas, permitindo farm infinito!"
                    : "On the main plugin tab, you choose which zones you want to farm.\n\n" +
                      "• Multi-Zone: When enabled, the plugin automatically teleports to the next selected zone once the current one is empty of FATEs.\n" +
                      "• World Rotation: If enabled, once the plugin cycles through all maps, it automatically travels to the next world in your list via Lifestream to continue grinding!");
                break;

            case 3:
                DrawHeader(isPt ? "Ajustes & Limite de Gemas" : "Automation & Cap Tuning");
                ImGui.Spacing();
                ImGui.Spacing();
                ImGui.TextUnformatted(isPt
                    ? "O FateFrenzy gerencia seu farm de ponta a ponta. Configure nas opções:\n\n" +
                      "• Troca de Gemas: Configure o plugin para gastar suas gemas automaticamente comprando Vouchers ou materiais quando estiver quase no limite (1500).\n" +
                      "• Fila de Classes: Adicione classes na fila. O plugin trocará de classe automaticamente quando a atual atingir o nível máximo.\n" +
                      "• Auto-Reparo: Repare seus itens automaticamente no mender do Grande Companhia ou usando matéria escura."
                    : "FateFrenzy manages your grind from end to end. Fine-tune these in settings:\n\n" +
                      "• Gemstone Spending: Keep your Bicolor Gemstones from capping (1500) by automatically trading them for Vouchers or materials.\n" +
                      "• Class Queue: Queue multiple classes. The plugin automatically switches to the next job when the current one reaches its level cap.\n" +
                      "• Auto-Repair: Keep your gear in top shape by automatically visiting repair NPCs or using Dark Matter.");
                break;

            case 4:
                DrawHeader(isPt ? "Tudo Pronto!" : "Ready to Start!");
                ImGui.Spacing();
                ImGui.Spacing();
                ImGui.TextUnformatted(isPt
                    ? "Você concluiu a introdução básica!\n\n" +
                      "Ao clicar em iniciar, o bot começará a farmar na primeira zona válida.\n\n" +
                      "• Auto-Pause: Se você entrar em alguma masmorra, raid ou conteúdo instanciado manualmente, o plugin pausará sozinho e continuará assim que você sair.\n\n" +
                      "Clique em 'Finish & Start' para ir à janela principal e começar seu farm!"
                    : "You have completed the basic introduction!\n\n" +
                      "When you click start, the bot will begin grinding in the first valid zone.\n\n" +
                      "• Auto-Pause: If you queue into a duty or enter instanced content, the plugin will automatically pause and safely resume once you return outside.\n\n" +
                      "Click 'Finish & Start' to access the main dashboard and begin your adventure!");
                break;
        }
    }

    private static string GetText(Configuration cfg, string key, string fallback)
    {
        var isPt = cfg.TutorialLanguage == "pt";
        switch (key)
        {
            case "skip": return isPt ? "Pular Tutorial" : "Skip Tutorial";
            case "prev": return isPt ? "Voltar" : "Back";
            case "next": return isPt ? "Próximo" : "Next";
            case "finish": return isPt ? "Concluir e Iniciar" : "Finish & Start";
            default: return fallback;
        }
    }
}
