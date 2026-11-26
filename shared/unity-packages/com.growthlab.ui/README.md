# ui

UI Toolkit 대시보드 구성 요소: Gauge, StatusBadge, StatCard, MessageList, 월드 라벨, 다크 테마(`Runtime/Resources/GrowthLabUI.uss`), 그래프.

## 그래프 (`GrowthLab.UI.Charts`)

데이터(스펙)와 그리기(요소)를 나눈다. 도메인 코드는 UI 없이 스펙만 만들고 테스트할 수 있다.

| 스펙 | 요소 | 용도 |
| --- | --- | --- |
| `BarChartSpec` | `BarChart` | 묶음 막대, 기준선(`Threshold`), 값 표시 |
| `LineChartSpec` | `LineChart` | 선·영역, 현재 위치 표시(`MarkerIndex`), 누적 S-커브 |
| `TimelineChartSpec` | `TimelineChart` | 간트 행, 확정(채움)·계획(테두리), 진척 채움, 마일스톤 ◆, 오늘 선 |
| `DonutChartSpec` | `DonutChart` | 진척 링(`DonutChartSpec.Progress`), 조각 비율 |

```csharp
var spec = new BarChartSpec { Title = "주간 가동률", Unit = "%", Max = 100, Threshold = 85, ThresholdLabel = "과부하 기준" };
spec.Categories.AddRange(new[] { "11-16", "11-23" });
spec.Series.Add(new ChartSeries("가동률", ChartPalette.Accent, new[] { 55f, 92f }));
var chart = Chart.Create(spec);   // 스펙 종류에 맞는 요소
panel.Add(chart);
chart.Animate(0.8f, delayMs: 200);   // 축에서부터 그려지며 나타남
chart.SetSpec(updated);              // 같은 종류의 새 데이터로 다시 그림(애니메이션 없음)
```

- 공통 기반 `ChartElement`가 제목·범례·캡션·빈 상태·포인터 툴팁·등장 애니메이션(`Reveal`)을 맡는다. 새 그래프는 이를 상속해 `DrawPlot`(Painter2D), `PlaceLabels`, `HitTest`만 구현하고 `Chart.Create`에 스펙을 연결한다.
- 축 계산은 `ChartScale`(눈금 최대값, 날짜 눈금, 비율)에 있다. 색은 `ChartPalette`, 격자·트랙 색은 USS 변수 `--gl-chart-grid`, `--gl-chart-track`이다.
