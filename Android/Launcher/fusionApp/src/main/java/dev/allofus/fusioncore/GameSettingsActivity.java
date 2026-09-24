package dev.allofus.fusioncore;

import android.content.pm.PackageInfo;
import android.content.pm.PackageManager;
import android.graphics.drawable.Drawable;
import android.os.Bundle;
import android.text.TextUtils;
import android.view.View;
import android.widget.AutoCompleteTextView;
import android.widget.ImageView;
import android.widget.TextView;
import android.widget.Toast;

import androidx.annotation.NonNull;
import androidx.annotation.Nullable;
import androidx.appcompat.app.AppCompatActivity;
import androidx.appcompat.content.res.AppCompatResources;
import androidx.core.content.pm.PackageInfoCompat;

import com.google.android.material.appbar.MaterialToolbar;



public class GameSettingsActivity extends AppCompatActivity {

    public static final String EXTRA_PACKAGE_NAME = "extra_package_name";

    private ImageView ivAppIcon;
    private TextView tvAppName;
    private TextView tvPackageName;
    private TextView tvVersionInfo;

    private AutoCompleteTextView actvOverrideActivity;

    private String targetPackageName;

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        setContentView(R.layout.activity_game_settings);

        initViews();
        setupToolbar();

        targetPackageName = getIntent().getStringExtra(EXTRA_PACKAGE_NAME);

        if (TextUtils.isEmpty(targetPackageName)) {
            Toast.makeText(this, getString(R.string.settings_no_target), Toast.LENGTH_SHORT).show();
            finish();
            return;
        }

        resolveAndDisplayPackageInfo(targetPackageName);
        // The launch activity is always resolved automatically, so the field is read-only.
        FusionSettings.dropActivityOverrideForGame(this, targetPackageName);
        actvOverrideActivity.setText(getString(R.string.settings_automatic), false);
        setupAdvancedFold();
        setupListeners();
    }

    private void setupAdvancedFold() {
        View header = findViewById(R.id.settings_advanced_header);
        View card = findViewById(R.id.settings_advanced_card);
        TextView chevron = findViewById(R.id.settings_advanced_chevron);
        header.setOnClickListener(v -> {
            boolean open = card.getVisibility() != View.VISIBLE;
            card.setVisibility(open ? View.VISIBLE : View.GONE);
            chevron.setText(open ? "▲" : "▼");
        });
    }

    private void initViews() {
        ivAppIcon = findViewById(R.id.ivAppIcon);
        tvAppName = findViewById(R.id.tvAppName);
        tvPackageName = findViewById(R.id.tvPackageName);
        tvVersionInfo = findViewById(R.id.tvVersionInfo);

        actvOverrideActivity = findViewById(R.id.activity_override_actv);
    }

    private void setupToolbar() {
        MaterialToolbar toolbar = findViewById(R.id.toolbar);
        toolbar.setNavigationOnClickListener(v -> finish());
    }

    private void resolveAndDisplayPackageInfo(String packageName) {
        PackageManager pm = getPackageManager();

        try {
            PackageInfo packageInfo = pm.getPackageInfo(packageName, PackageManager.GET_ACTIVITIES | PackageManager.MATCH_DISABLED_COMPONENTS);

            CharSequence appLabel;
            Drawable appIcon;
            if (packageInfo.applicationInfo != null) {
                appLabel = pm.getApplicationLabel(packageInfo.applicationInfo);
                appIcon = pm.getApplicationIcon(packageInfo.applicationInfo);
            } else {
                appLabel = packageName;
                appIcon = AppCompatResources.getDrawable(this, R.drawable.android_48px);
            }

            String versionName = packageInfo.versionName != null ? packageInfo.versionName : "N/A";
            long versionCode = PackageInfoCompat.getLongVersionCode(packageInfo);

            tvAppName.setText(appLabel);
            tvPackageName.setText(packageName);
            tvVersionInfo.setText(getString(R.string.settings_version_format, versionName, versionCode));
            ivAppIcon.setImageDrawable(appIcon);
        } catch (PackageManager.NameNotFoundException e) {
            Toast.makeText(this, getString(R.string.settings_package_not_found, packageName), Toast.LENGTH_SHORT).show();
            finish();
        }
    }

    private void setupListeners() {
    }
}