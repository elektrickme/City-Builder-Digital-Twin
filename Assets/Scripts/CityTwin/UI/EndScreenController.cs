using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using DG.Tweening;
using CityTwin.Core;

namespace CityTwin.UI
{
    /// <summary>
    /// Owns the end-of-session screen UI: the overlay panel, title/body text,
    /// and the restart status line used by RestartFlowController.
    /// Other components (TooltipService, RestartFlowController) drive this via its public API
    /// instead of holding direct references to the UI fields.
    /// </summary>
    public class EndScreenController : MonoBehaviour
    {
        [SerializeField] private DashboardController _dashboardController;
        
        [Header("End Screen UI")]
        [Tooltip("Root GameObject of the end screen overlay. Toggled on when the session ends, off on restart.")]
        [SerializeField] private GameObject endPanel;
        [SerializeField] private TextMeshProUGUI endTitleText;
        [SerializeField] private TextMeshProUGUI endBodyText;
        [SerializeField] private TextMeshProUGUI QOLText;

        [Header("Restart Flow UI")]
        [Tooltip("Text shown during the restart flow (remove-tiles prompt, then countdown).")]
        [SerializeField] private TextMeshProUGUI restartStatusText;

        [Header("Restart Status Pulse")]
        [Tooltip("Gently pulse the restart status line while it shows a message (e.g. the clear-tiles prompt).")]
        [SerializeField] private bool pulseRestartStatus = true;
        [Tooltip("Peak scale of the pulse relative to the resting scale.")]
        [SerializeField] private float pulseScaleAmount = 1.06f;
        [Tooltip("Seconds for one half of the pulse (grow or shrink). Full breath = twice this.")]
        [SerializeField] private float pulseHalfPeriodSeconds = 0.6f;

        [Header("Restart Status Glow")]
        [Tooltip("Gentle HDR glow breath on the completion/restart message (nothing moves; brightness breathes and the bloom pass halos it).")]
        [SerializeField] private bool glowRestartStatus = true;
        [Tooltip("Glow at the dim end of the breath (must clear the bloom threshold, ~2.24, to be visible).")]
        [SerializeField] private float statusGlowMin = 2.3f;
        [Tooltip("Glow at the bright end of the breath.")]
        [SerializeField] private float statusGlowMax = 2.9f;
        [Tooltip("Seconds for one full breath (dim -> bright -> dim).")]
        [SerializeField] private float statusGlowPeriod = 4f;

        [Header("Completion Phase")]
        [Tooltip("Seconds the scorecard (score, cards, band text) stays up before it gives way to the big completion message. The restart status text is hidden until then.")]
        [SerializeField] private float scorecardSeconds = 12f;
        [Tooltip("The card's background image. Auto-resolved by name (\"Background\") when left empty.")]
        [SerializeField] private Image cardBackground;
        [Tooltip("Level visuals switched off while the completion message shows, leaving only the placed-tile pins and the message. Hidden via CanvasGroup alpha, so no object lifecycles are touched.")]
        [SerializeField] private GameObject[] hideOnCompletion;
        [Tooltip("The map image itself. It parents the pins, so its component is disabled rather than alpha-fading the whole subtree.")]
        [SerializeField] private Graphic mapGraphic;
        [Tooltip("Seconds per fade stage of the end transition: (1) map+UI out / roads dim, (2) roads+particles out, (3) message up.")]
        [SerializeField] private float completionFadeSeconds = 0.8f;
        [Tooltip("Road network alpha during stage 1, before it fades out fully in stage 2.")]
        [Range(0f, 1f)] [SerializeField] private float completionRoadDim = 0.4f;

        [Header("End Body Paragraph Cycle")]
        [Tooltip("Freeze the end-report body to its first paragraph (the verdict). Pro-tip paragraphs are gameplay advice and stay off the final report. Overrides the cycle below.")]
        [SerializeField] private bool freezeBodyToFirstParagraph = true;
        [Tooltip("When the end body holds multiple paragraphs (blank-line separated), page through them one at a time instead of cramming everything into the box at an unreadable size.")]
        [SerializeField] private bool cycleBodyParagraphs = true;
        [Tooltip("Seconds each paragraph stays on screen before switching to the next.")]
        [SerializeField] private float bodyParagraphSeconds = 5f;
        [Tooltip("Fade seconds when paragraphs switch.")]
        [SerializeField] private float bodyFadeSeconds = 0.35f;

        [Header("End Report Cards")]
        [Tooltip("Body text of the Balance card (feedback.* localization). Set by TutorialSequenceController.")]
        [SerializeField] private TextMeshProUGUI balanceBodyText;
        [Tooltip("Body text of the Strategic card (reaction.*.access.v2 localization). Set by TutorialSequenceController.")]
        [SerializeField] private TextMeshProUGUI strategicBodyText;
        [Tooltip("Body text of the Budget card: remaining budget line. Set by TutorialSequenceController.")]
        [SerializeField] private TextMeshProUGUI budgetBodyText;

        public bool IsVisible => endPanel != null && endPanel.activeSelf;

        /// <summary>Activate the overlay and fill in the final title/body text. Clears any prior restart status.
        /// Starts in the scorecard phase; after <see cref="scorecardSeconds"/> the scorecard yields to the
        /// big completion/restart message (see <see cref="CompletionPhaseRoutine"/>).</summary>
        public void Show(string title, string body)
        {
            if (endPanel != null) endPanel.SetActive(true);
            if (endTitleText != null) endTitleText.text = title ?? string.Empty;
            SetBody(body ?? string.Empty);

            //Set QOL Score
            QOLText.text = Mathf.RoundToInt(_dashboardController.DisplayQol).ToString();

            SetRestartStatus(string.Empty);
            EnterScorecardPhase();
        }

        /// <summary>Hide the overlay and clear the restart status.</summary>
        public void Hide()
        {
            StopBodyCycle();
            StopCompletionPhase();
            if (endPanel != null) endPanel.SetActive(false);
            SetRestartStatus(string.Empty);
        }

        // ---- scorecard -> completion phase ----

        private Coroutine _completionRoutine;
        private List<GameObject> _scorecardElements;

        /// <summary>Everything that belongs to the scorecard view: score, cards, band title/body.
        /// Resolved from the serialized refs (card roots are the parents of their body texts); the
        /// unreferenced "Your score" caption is picked up by name next to the score.</summary>
        private List<GameObject> ScorecardElements()
        {
            if (_scorecardElements != null) return _scorecardElements;
            _scorecardElements = new List<GameObject>();
            void Add(Component c) { if (c != null) _scorecardElements.Add(c.gameObject); }
            Add(QOLText);
            Add(endTitleText);
            Add(endBodyText);
            if (balanceBodyText != null) Add(balanceBodyText.transform.parent);
            if (strategicBodyText != null) Add(strategicBodyText.transform.parent);
            if (budgetBodyText != null) Add(budgetBodyText.transform.parent);
            if (QOLText != null && QOLText.transform.parent != null)
            {
                var caption = QOLText.transform.parent.Find("Your score Text");
                if (caption != null) _scorecardElements.Add(caption.gameObject);
                // The report heading belongs to the scorecard too: on the completion screen only
                // the pins and the clear-the-table message may remain.
                var heading = QOLText.transform.parent.Find("Simulation Complete Text");
                if (heading != null) _scorecardElements.Add(heading.gameObject);
            }
            // The android mascot leaves with the scorecard — the clear-the-table message stands alone.
            if (endPanel != null)
            {
                foreach (var t in endPanel.GetComponentsInChildren<Transform>(true))
                    if (t.name == "Avatar Icon") { _scorecardElements.Add(t.gameObject); break; }
            }
            return _scorecardElements;
        }

        private float _cardBaseAlpha = -1f;

        private Image CardBackground()
        {
            if (cardBackground == null && endPanel != null)
            {
                foreach (var img in endPanel.GetComponentsInChildren<Image>(true))
                    if (img.name == "Background") { cardBackground = img; break; }
            }
            if (cardBackground != null && _cardBaseAlpha < 0f) _cardBaseAlpha = cardBackground.color.a;
            return cardBackground;
        }

        private void SetCardAlpha(float a)
        {
            var bg = CardBackground();
            if (bg == null) return;
            var c = bg.color;
            c.a = a;
            bg.color = c;
        }

        private void EnterScorecardPhase()
        {
            StopCompletionPhase();
            foreach (var go in ScorecardElements())
            {
                if (go == null) continue;
                go.SetActive(true);
                ResetGroup(go); // undo any half-finished fade from a previous transition
            }
            // The restart prompt stays out of sight while the player reads their results.
            if (restartStatusText != null) restartStatusText.gameObject.SetActive(false);
            // Inactive instances (the pooled quadrant clones) can't run coroutines and don't render
            // anyway — leave them parked in the scorecard state instead of logging errors.
            if (isActiveAndEnabled)
                _completionRoutine = StartCoroutine(CompletionPhaseRoutine());
        }

        /// <summary>Staged end transition: (1) scorecard + map + UI fade out while the road network
        /// dims, (2) roads and their flow particles fade away, (3) the clear-the-table message fades
        /// up over the translucent card. Only the placed-tile pins survive all three stages.</summary>
        private IEnumerator CompletionPhaseRoutine()
        {
            yield return new WaitForSecondsRealtime(Mathf.Max(1f, scorecardSeconds));
            StopBodyCycle();
            float stage = Mathf.Max(0.1f, completionFadeSeconds);

            // Stage 1: scorecard and level UI fade out; the road network only dims for now.
            // The map background PNG fades with them (its Image color doesn't cascade to the
            // pins it parents, so they stay visible).
            foreach (var go in ScorecardElements()) FadeGroup(go, 0f, stage);
            foreach (var go in hideOnCompletion)
                FadeGroup(go, IsRoadHolder(go) ? completionRoadDim : 0f, stage);
            var map = MapGraphic();
            if (map != null)
            {
                DOTween.Kill(map);
                map.DOFade(0f, stage).SetEase(Ease.InOutSine).SetUpdate(true).SetTarget(map);
            }
            yield return new WaitForSecondsRealtime(stage);

            // Stage 2: the roads and their particles go too.
            foreach (var go in hideOnCompletion)
                if (IsRoadHolder(go)) FadeGroup(go, 0f, stage);
            yield return new WaitForSecondsRealtime(stage);

            // Stage 3: the card backdrop disappears with the scorecard — the clear-the-table
            // message floats free over the pins. The faded map image is switched off entirely
            // so nothing of the level PNG lingers either.
            foreach (var go in ScorecardElements()) if (go != null) go.SetActive(false);
            if (map != null) map.enabled = false;
            var cardBg = CardBackground();
            if (cardBg != null)
            {
                DOTween.Kill(cardBg);
                cardBg.DOFade(0f, stage).SetEase(Ease.InOutSine).SetUpdate(true).SetTarget(cardBg)
                    .OnComplete(() => { if (cardBg != null) cardBg.enabled = false; });
            }
            if (restartStatusText != null)
            {
                restartStatusText.gameObject.SetActive(true);
                var cg = GetOrAddGroup(restartStatusText.gameObject);
                DOTween.Kill(cg);
                cg.alpha = 0f;
                cg.DOFade(1f, stage).SetEase(Ease.InOutSine).SetUpdate(true).SetTarget(cg);
            }
            _completionRoutine = null;
        }

        private void StopCompletionPhase()
        {
            if (_completionRoutine != null) { StopCoroutine(_completionRoutine); _completionRoutine = null; }
            if (restartStatusText != null)
            {
                restartStatusText.gameObject.SetActive(true);
                ResetGroup(restartStatusText.gameObject);
            }
            var bg = CardBackground();
            if (bg != null) { DOTween.Kill(bg); bg.enabled = true; }
            if (_cardBaseAlpha >= 0f) SetCardAlpha(_cardBaseAlpha); // back to the opaque scorecard
            RestoreLevelVisuals();
        }

        // ---- completion fade helpers ----

        private static bool IsRoadHolder(GameObject go)
            => go != null && go.name.Contains("Connector Lines");

        private Graphic _resolvedMapGraphic;
        private float _mapBaseAlpha = -1f;

        /// <summary>The visible map background PNG. The serialized field wins only when it points
        /// at a live component; otherwise fall back to the "Main BG" Image, which is where
        /// HubLayoutManager puts the map art (the old serialized target was a disabled black
        /// backdrop, so "disable the background" silently did nothing).</summary>
        private Graphic MapGraphic()
        {
            if (_resolvedMapGraphic != null) return _resolvedMapGraphic;
            if (mapGraphic != null && mapGraphic.enabled)
            {
                _resolvedMapGraphic = mapGraphic;
            }
            else
            {
                var root = GetComponentInParent<GameInstanceRoot>(true);
                Transform searchFrom = root != null ? root.transform : transform.root;
                foreach (var img in searchFrom.GetComponentsInChildren<Image>(true))
                    if (img.name == "Main BG") { _resolvedMapGraphic = img; break; }
            }
            if (_resolvedMapGraphic != null && _mapBaseAlpha < 0f)
                _mapBaseAlpha = _resolvedMapGraphic.color.a;
            return _resolvedMapGraphic;
        }

        private static CanvasGroup GetOrAddGroup(GameObject go)
        {
            var cg = go.GetComponent<CanvasGroup>();
            if (cg == null) cg = go.AddComponent<CanvasGroup>();
            return cg;
        }

        private static void FadeGroup(GameObject go, float target, float seconds)
        {
            if (go == null) return;
            var cg = GetOrAddGroup(go);
            DOTween.Kill(cg);
            cg.blocksRaycasts = target > 0.99f;
            cg.DOFade(target, seconds).SetEase(Ease.InOutSine).SetUpdate(true).SetTarget(cg);
        }

        private static void ResetGroup(GameObject go)
        {
            if (go == null) return;
            var cg = go.GetComponent<CanvasGroup>();
            if (cg == null) return;
            DOTween.Kill(cg);
            cg.alpha = 1f;
            cg.blocksRaycasts = true;
        }

        /// <summary>Instant restore of everything the staged fade touched (new session incoming).</summary>
        private void RestoreLevelVisuals()
        {
            if (mapGraphic != null) mapGraphic.enabled = true;
            var map = MapGraphic();
            if (map != null)
            {
                DOTween.Kill(map);
                map.enabled = true;
                if (_mapBaseAlpha >= 0f)
                {
                    var c = map.color;
                    c.a = _mapBaseAlpha;
                    map.color = c;
                }
            }
            if (hideOnCompletion == null) return;
            foreach (var go in hideOnCompletion) ResetGroup(go);
        }

        // ---- body paragraph cycle ----

        private Coroutine _bodyCycle;
        private List<string> _bodyParagraphs;
        private float _bodyBaseAlpha = -1f;

        /// <summary>Multi-paragraph bodies (summary + Pro-Tip) page one paragraph at a time so each
        /// renders at a readable size; single-paragraph bodies are shown directly.</summary>
        private void SetBody(string body)
        {
            StopBodyCycle();
            if (endBodyText == null) return;

            var paragraphs = new List<string>();
            foreach (var p in System.Text.RegularExpressions.Regex.Split(body, @"\n\s*\n"))
            {
                string trimmed = p.Trim();
                if (trimmed.Length > 0) paragraphs.Add(trimmed);
            }

            if (freezeBodyToFirstParagraph && paragraphs.Count > 0)
            {
                // Frozen report: just the verdict paragraph, auto-sized to the full box.
                endBodyText.enableAutoSizing = true;
                endBodyText.text = paragraphs[0];
                return;
            }

            if (!cycleBodyParagraphs || paragraphs.Count <= 1 || !isActiveAndEnabled)
            {
                endBodyText.enableAutoSizing = true; // single text: let auto-size do its thing
                endBodyText.text = body;
                return;
            }

            // Pin one common font size for the whole cycle: auto-fit each paragraph, keep the
            // smallest result. Otherwise short tips render bigger than long ones and the text
            // visibly jumps in size on every switch.
            float common = float.MaxValue;
            endBodyText.enableAutoSizing = true;
            foreach (var p in paragraphs)
            {
                endBodyText.text = p;
                endBodyText.ForceMeshUpdate(true, true);
                common = Mathf.Min(common, endBodyText.fontSize);
            }
            endBodyText.enableAutoSizing = false;
            endBodyText.fontSize = common;

            _bodyParagraphs = paragraphs;
            if (_bodyBaseAlpha < 0f) _bodyBaseAlpha = endBodyText.color.a;
            _bodyCycle = StartCoroutine(BodyCycleRoutine());
        }

        private void StopBodyCycle()
        {
            if (_bodyCycle != null) { StopCoroutine(_bodyCycle); _bodyCycle = null; }
            if (endBodyText != null && _bodyBaseAlpha >= 0f) SetBodyAlpha(_bodyBaseAlpha);
        }

        private IEnumerator BodyCycleRoutine()
        {
            int i = 0;
            while (true)
            {
                endBodyText.text = _bodyParagraphs[i % _bodyParagraphs.Count];
                yield return FadeBody(0f, _bodyBaseAlpha);
                yield return new WaitForSecondsRealtime(Mathf.Max(1f, bodyParagraphSeconds));
                yield return FadeBody(_bodyBaseAlpha, 0f);
                i++;
            }
        }

        private IEnumerator FadeBody(float from, float to)
        {
            float d = Mathf.Max(0.01f, bodyFadeSeconds);
            for (float t = 0f; t < d; t += Time.unscaledDeltaTime)
            {
                SetBodyAlpha(Mathf.Lerp(from, to, t / d));
                yield return null;
            }
            SetBodyAlpha(to);
        }

        private void SetBodyAlpha(float a)
        {
            var c = endBodyText.color;
            c.a = a;
            endBodyText.color = c;
        }

        /// <summary>Update the restart status line (e.g. "Please remove all tiles" / "Restarting in 3...").
        /// Pulses while non-empty; text changes mid-pulse do not restart the loop.</summary>
        public void SetRestartStatus(string message)
        {
            if (restartStatusText == null) return;
            restartStatusText.text = message ?? string.Empty;
            if (!string.IsNullOrEmpty(message))
            {
                if (pulseRestartStatus) StartStatusPulse();
                if (glowRestartStatus) StartStatusGlow();
            }
            else
            {
                StopStatusPulse();
                StopStatusGlow();
            }
        }

        private Tween _statusPulse;
        private Vector3 _statusBaseScale = Vector3.one;
        private bool _statusBaseScaleCaptured;

        private void StartStatusPulse()
        {
            if (_statusPulse != null && _statusPulse.IsActive()) return;   // already breathing

            Transform t = restartStatusText.transform;
            // Capture the resting scale once, before any pulse, so repeated prompts never compound.
            if (!_statusBaseScaleCaptured)
            {
                _statusBaseScale = t.localScale;
                _statusBaseScaleCaptured = true;
            }
            t.localScale = _statusBaseScale;
            _statusPulse = t.DOScale(_statusBaseScale * pulseScaleAmount, pulseHalfPeriodSeconds)
                .SetEase(Ease.InOutSine)
                .SetLoops(-1, LoopType.Yoyo)
                .SetUpdate(true)
                .SetTarget(t);
        }

        private void StopStatusPulse()
        {
            if (_statusPulse != null) { _statusPulse.Kill(); _statusPulse = null; }
            if (restartStatusText != null && _statusBaseScaleCaptured)
                restartStatusText.transform.localScale = _statusBaseScale;
        }

        // ---- status glow breath ----

        private Tween _statusGlow;
        private UIGlow _statusGlowComp;

        /// <summary>Slow HDR breath on the status text: brightness sweeps statusGlowMin..Max so the
        /// bloom pass gives the message a living halo without any motion.</summary>
        private void StartStatusGlow()
        {
            if (_statusGlow != null && _statusGlow.IsActive()) return; // already breathing

            if (_statusGlowComp == null)
            {
                _statusGlowComp = restartStatusText.GetComponent<UIGlow>();
                if (_statusGlowComp == null)
                {
                    _statusGlowComp = restartStatusText.gameObject.AddComponent<UIGlow>();
                    _statusGlowComp.glowBoost = Mathf.Max(1f, statusGlowMin);
                }
            }

            _statusGlowComp.GlowBoost = Mathf.Max(1f, statusGlowMin);
            float half = Mathf.Max(0.25f, statusGlowPeriod * 0.5f);
            _statusGlow = DOTween.To(() => _statusGlowComp.GlowBoost, v => _statusGlowComp.GlowBoost = v,
                    Mathf.Max(statusGlowMax, statusGlowMin), half)
                .SetEase(Ease.InOutSine)
                .SetLoops(-1, LoopType.Yoyo)
                .SetUpdate(true)
                .SetTarget(_statusGlowComp);
        }

        private void StopStatusGlow()
        {
            if (_statusGlow != null) { _statusGlow.Kill(); _statusGlow = null; }
            if (_statusGlowComp != null) _statusGlowComp.GlowBoost = _statusGlowComp.BaseGlowBoost;
        }

        private void OnDisable()
        {
            StopStatusPulse();
            StopStatusGlow();
            // Coroutines die with the component; just restore the faded alpha.
            _bodyCycle = null;
            if (endBodyText != null && _bodyBaseAlpha >= 0f) SetBodyAlpha(_bodyBaseAlpha);
        }

        /// <summary>Fill the end-report card bodies (Balance, Strategic). QOL is rendered by <see cref="Show"/>;
        /// this only populates the cards, so call order with Show does not matter. Card titles are static UI.</summary>
        public void SetReport(string balanceBody, string strategicBody)
        {
            if (balanceBodyText != null) balanceBodyText.text = balanceBody ?? string.Empty;
            if (strategicBodyText != null) strategicBodyText.text = strategicBody ?? string.Empty;
            UnifyFontSizes(balanceBodyText, strategicBodyText, budgetBodyText);
        }

        /// <summary>Fill all three report cards, including the Budget card's remaining-budget line.</summary>
        public void SetReport(string balanceBody, string strategicBody, string budgetBody)
        {
            if (budgetBodyText != null) budgetBodyText.text = budgetBody ?? string.Empty;
            SetReport(balanceBody, strategicBody); // unifies sizes across all three cards
        }

        /// <summary>Auto-fit each text, then pin all to the smallest fitted size so sibling cards
        /// read at one consistent size instead of each card picking its own.</summary>
        private static void UnifyFontSizes(params TextMeshProUGUI[] texts)
        {
            float common = float.MaxValue;
            foreach (var t in texts)
            {
                if (t == null || string.IsNullOrEmpty(t.text)) continue;
                t.enableAutoSizing = true;
                t.ForceMeshUpdate(true, true);
                common = Mathf.Min(common, t.fontSize);
            }
            if (common == float.MaxValue) return;
            foreach (var t in texts)
            {
                if (t == null) continue;
                t.enableAutoSizing = false;
                t.fontSize = common;
            }
        }
    }
}
