using System;
using UnityEngine;

namespace ProjectSoullike
{
    [DisallowMultipleComponent]
    public sealed class PlayerStamina : MonoBehaviour
    {
        [Header("Stamina")]
        [SerializeField, Min(1f)] private float maxStamina = 100f;
        [SerializeField, Min(0f)] private float regenPerSecond = 32f;
        [SerializeField, Min(0f)] private float regenDelay = 0.8f;
        [SerializeField, Range(0f, 1f)] private float exhaustedRecoverRatio = 0.25f;

        [Header("Temporary Stamina Bar")]
        [SerializeField] private bool showStaminaBar = true;
        [SerializeField, Min(80f)] private float staminaBarWidth = 240f;
        [SerializeField, Min(4f)] private float staminaBarHeight = 10f;

        private float _currentStamina;
        private float _regenBlockedTimer;

        public event Action<float, float> StaminaChanged;
        public event Action<float> ActionRejected;
        public event Action Exhausted;

        public float CurrentStamina => _currentStamina;
        public float MaxStamina => maxStamina;
        public bool IsExhausted { get; private set; }
        // 임시 바 표시. CombatHud가 있으면 끈다.
        public bool ShowStaminaBar { get => showStaminaBar; set => showStaminaBar = value; }

        private void Awake()
        {
            // OnGUI는 GUILayout을 쓰지 않으므로 레이아웃 이벤트를 건너뛴다(멘토링 P2 프로파일: OnGUI가 프레임마다 두 번 불리던 비용).
            useGUILayout = false;
            ResetStamina();
        }

        private void Update()
        {
            if (_regenBlockedTimer > 0f)
            {
                _regenBlockedTimer -= Time.deltaTime;
                return;
            }

            if (_currentStamina >= maxStamina)
            {
                return;
            }

            SetStamina(_currentStamina + regenPerSecond * Time.deltaTime);
            if (IsExhausted && _currentStamina >= maxStamina * exhaustedRecoverRatio)
            {
                IsExhausted = false;
            }
        }

        public bool CanAfford(float cost)
        {
            return cost <= 0f || (!IsExhausted && _currentStamina >= cost);
        }

        public bool TryConsume(float cost)
        {
            if (cost <= 0f)
            {
                return true;
            }

            if (!CanAfford(cost))
            {
                ActionRejected?.Invoke(cost);
                return false;
            }

            Spend(cost);
            return true;
        }

        public bool Drain(float amount)
        {
            if (IsExhausted || _currentStamina <= 0f)
            {
                return false;
            }

            Spend(Mathf.Min(amount, _currentStamina));
            return !IsExhausted;
        }

        public void ResetStamina()
        {
            _regenBlockedTimer = 0f;
            IsExhausted = false;
            SetStamina(maxStamina);
        }

        private void Spend(float amount)
        {
            _regenBlockedTimer = regenDelay;
            SetStamina(_currentStamina - amount);

            if (_currentStamina <= 0f && !IsExhausted)
            {
                IsExhausted = true;
                Exhausted?.Invoke();
            }
        }

        private void SetStamina(float value)
        {
            float clamped = Mathf.Clamp(value, 0f, maxStamina);
            if (Mathf.Approximately(clamped, _currentStamina))
            {
                _currentStamina = clamped;
                return;
            }

            _currentStamina = clamped;
            StaminaChanged?.Invoke(_currentStamina, maxStamina);
        }

        private void OnGUI()
        {
            if (!showStaminaBar)
            {
                return;
            }

            float uiScale = Mathf.Clamp(Screen.height / 1080f, 0.7f, 1.4f);
            float width = staminaBarWidth * uiScale;
            float height = staminaBarHeight * uiScale;
            Rect backgroundRect = new Rect(24f * uiScale, 48f * uiScale, width, height);
            Rect fillRect = new Rect(
                backgroundRect.x + 2f,
                backgroundRect.y + 2f,
                Mathf.Max(0f, (backgroundRect.width - 4f) * (_currentStamina / maxStamina)),
                Mathf.Max(0f, backgroundRect.height - 4f));

            Color previousColor = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.85f);
            GUI.DrawTexture(backgroundRect, Texture2D.whiteTexture);
            GUI.color = IsExhausted
                ? new Color(0.45f, 0.45f, 0.2f, 1f)
                : new Color(0.2f, 0.75f, 0.25f, 1f);
            GUI.DrawTexture(fillRect, Texture2D.whiteTexture);
            GUI.color = previousColor;
        }
    }
}
