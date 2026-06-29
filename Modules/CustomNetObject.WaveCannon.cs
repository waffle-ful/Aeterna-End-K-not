using UnityEngine;

namespace EndKnot
{
    internal sealed class WaveCannonWarning : CustomNetObject
    {
        public WaveCannonWarning(Vector2 position, string sprite)
        {
            CreateNetObject(sprite, position);
        }

        public override void OnMeeting() => Despawn();
    }

    internal sealed class WaveCannonBeamSegment : CustomNetObject
    {
        // ビームは Firing 中ずっと毎フレ TP で揺らす→ ForceSnapSend 連射になるので base の opt-in 間引きで 10Hz 固定(揺れを滑らかに保ちつつ anti-cheat 安全)。
        protected override float ForceSnapMinInterval => 0.1f;

        public WaveCannonBeamSegment(Vector2 position, string sprite)
        {
            CreateNetObject(sprite, position);
        }

        public override void OnMeeting() => Despawn();
    }

    internal sealed class WaveCannonGate : CustomNetObject
    {
        public WaveCannonGate(Vector2 position, string borderColor = "#5e1a00", string midColor = "#ff7a00", string centerColor = "#ffaa00")
        {
            CreateNetObject("<size=252%><line-height=67%>" +
                $"<alpha=#00>█<{borderColor}>█<{borderColor}>█<{borderColor}>█<{borderColor}>█<alpha=#00>█<br>" +
                $"<alpha=#00>█<{borderColor}>█<{midColor}>█<{midColor}>█<{borderColor}>█<alpha=#00>█<br>" +
                $"<alpha=#00>█<{borderColor}>█<{centerColor}>█<{centerColor}>█<{borderColor}>█<alpha=#00>█<br>" +
                $"<alpha=#00>█<{borderColor}>█<{centerColor}>█<{centerColor}>█<{borderColor}>█<alpha=#00>█<br>" +
                $"<alpha=#00>█<{borderColor}>█<{midColor}>█<{midColor}>█<{borderColor}>█<alpha=#00>█<br>" +
                $"<alpha=#00>█<{borderColor}>█<{borderColor}>█<{borderColor}>█<{borderColor}>█<alpha=#00>█<br>" +
                "</line-height></size>", position);
        }

        public override void OnMeeting() => Despawn();
    }
}
