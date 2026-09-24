package dev.allofus.fusioncore.ui;

import android.animation.ValueAnimator;
import android.content.Context;
import android.graphics.Canvas;
import android.graphics.Paint;
import android.graphics.RadialGradient;
import android.graphics.Shader;
import android.util.AttributeSet;
import android.view.View;
import android.view.animation.AccelerateDecelerateInterpolator;

import androidx.annotation.Nullable;
import androidx.core.content.ContextCompat;

import dev.allofus.fusioncore.R;

/** A single ember that breathes while the game is being prepared. */
public class EmberView extends View {
    private final Paint glowPaint = new Paint(Paint.ANTI_ALIAS_FLAG);
    private final Paint corePaint = new Paint(Paint.ANTI_ALIAS_FLAG);
    private final int ember;
    private float breath = 0.6f;
    private ValueAnimator animator;

    public EmberView(Context context) {
        this(context, null);
    }

    public EmberView(Context context, @Nullable AttributeSet attrs) {
        super(context, attrs);
        ember = ContextCompat.getColor(context, R.color.ek_ember);
        corePaint.setColor(0xFFFFDEA0);
    }

    @Override
    protected void onVisibilityChanged(View changedView, int visibility) {
        super.onVisibilityChanged(changedView, visibility);
        update();
    }

    @Override
    protected void onAttachedToWindow() {
        super.onAttachedToWindow();
        update();
    }

    @Override
    protected void onDetachedFromWindow() {
        stop();
        super.onDetachedFromWindow();
    }

    private void update() {
        if (isShown() && Motion.enabled(getContext())) {
            if (animator == null) {
                animator = ValueAnimator.ofFloat(0.35f, 1f);
                animator.setDuration(1400);
                animator.setRepeatMode(ValueAnimator.REVERSE);
                animator.setRepeatCount(ValueAnimator.INFINITE);
                animator.setInterpolator(new AccelerateDecelerateInterpolator());
                animator.addUpdateListener(a -> {
                    breath = (float) a.getAnimatedValue();
                    invalidate();
                });
                animator.start();
            }
        } else {
            stop();
        }
    }

    private void stop() {
        if (animator != null) {
            animator.cancel();
            animator = null;
        }
        breath = 0.6f;
    }

    @Override
    protected void onDraw(Canvas canvas) {
        float cx = getWidth() / 2f;
        float cy = getHeight() / 2f;
        float max = Math.min(cx, cy);
        float glowR = max * (0.55f + 0.45f * breath);
        glowPaint.setShader(new RadialGradient(cx, cy, glowR,
                new int[] {FuseView.withAlpha(ember, 0.9f), FuseView.withAlpha(ember, 0.25f * breath + 0.1f), FuseView.withAlpha(ember, 0f)},
                new float[] {0f, 0.3f, 1f}, Shader.TileMode.CLAMP));
        canvas.drawCircle(cx, cy, glowR, glowPaint);
        canvas.drawCircle(cx, cy, max * (0.1f + 0.03f * breath), corePaint);
    }
}
